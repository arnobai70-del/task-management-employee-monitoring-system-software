#!/usr/bin/env bash
set -euo pipefail

if [[ $# -gt 1 ]]; then
  echo "Usage: $0 [production-env-file]" >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ $# -eq 1 ]]; then
  export ENV_FILE="$1"
fi
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

require_command curl
require_command python3
require_production_variables

check_host="${OBSERVABILITY_CHECK_HOST:-127.0.0.1}"
prometheus_url="http://${check_host}:${PROMETHEUS_PORT:-9090}"
loki_url="http://${check_host}:${LOKI_PORT:-3100}"
grafana_url="http://${check_host}:${GRAFANA_PORT:-3000}"
collector_url="http://${check_host}:${OTEL_HEALTH_PORT:-13133}"

wait_http() {
  local label="$1"
  local url="$2"
  local attempts="${3:-60}"
  for ((i = 1; i <= attempts; i++)); do
    if curl --fail --silent --show-error --max-time 5 "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done
  echo "$label did not become healthy: $url" >&2
  return 1
}

json_query_has_result() {
  python3 -c 'import json,sys; payload=json.load(sys.stdin); result=payload.get("data", {}).get("result", []); raise SystemExit(0 if payload.get("status")=="success" and result else 1)'
}

wait_http "OpenTelemetry collector" "$collector_url/"
wait_http "Prometheus" "$prometheus_url/-/ready"
wait_http "Loki" "$loki_url/ready"
wait_http "Grafana" "$grafana_url/api/health"
wait_for_app_ready 60

# Generate normal request telemetry without authentication or query-string data.
for _ in {1..5}; do
  curl --fail --silent --show-error --max-time 10 "${APP_PUBLIC_URL%/}/openapi/v1.json" >/dev/null
  sleep 1
done

metric_ok=0
for _ in {1..45}; do
  if curl --fail --silent --show-error --max-time 10 --get \
      --data-urlencode 'query=taskmonitoring_api_up' \
      "$prometheus_url/api/v1/query" | json_query_has_result; then
    metric_ok=1
    break
  fi
  sleep 2
done
if [[ "$metric_ok" -ne 1 ]]; then
  echo "Prometheus did not receive taskmonitoring_api_up from the API." >&2
  exit 1
fi

request_metric_ok=0
for _ in {1..30}; do
  if curl --fail --silent --show-error --max-time 10 --get \
      --data-urlencode 'query=sum(taskmonitoring_api_requests_total)' \
      "$prometheus_url/api/v1/query" | json_query_has_result; then
    request_metric_ok=1
    break
  fi
  sleep 2
done
if [[ "$request_metric_ok" -ne 1 ]]; then
  echo "Prometheus did not receive TaskMonitoring API request counters." >&2
  exit 1
fi

rules_payload="$(curl --fail --silent --show-error --max-time 10 "$prometheus_url/api/v1/rules")"
if ! python3 -c 'import json,sys; payload=json.load(sys.stdin); names={rule.get("name") for group in payload.get("data",{}).get("groups",[]) for rule in group.get("rules",[])}; required={"TaskMonitoringApiTelemetryMissing","TaskMonitoringCollectorScrapeDown","TaskMonitoringLokiScrapeDown","TaskMonitoringApiHighServerErrorRate"}; raise SystemExit(0 if required.issubset(names) else 1)' <<<"$rules_payload"; then
  echo "Prometheus alert rules are missing or failed to load." >&2
  exit 1
fi

logs_ok=0
for _ in {1..45}; do
  if curl --fail --silent --show-error --max-time 10 --get \
      --data-urlencode 'query={service_name="TaskMonitoring.Api"}' \
      --data-urlencode 'limit=20' \
      --data-urlencode 'since=10m' \
      "$loki_url/loki/api/v1/query_range" | json_query_has_result; then
    logs_ok=1
    break
  fi
  sleep 2
done
if [[ "$logs_ok" -ne 1 ]]; then
  echo "Loki did not receive TaskMonitoring API logs through the OpenTelemetry collector." >&2
  exit 1
fi

grafana_health="$(curl --fail --silent --show-error --max-time 10 "$grafana_url/api/health")"
if ! python3 -c 'import json,sys; payload=json.load(sys.stdin); raise SystemExit(0 if payload.get("database")=="ok" else 1)' <<<"$grafana_health"; then
  echo "Grafana health response did not report database=ok." >&2
  exit 1
fi

echo "Observability acceptance passed: API OTLP metrics/logs, Prometheus rules, Loki storage and Grafana health are working."
