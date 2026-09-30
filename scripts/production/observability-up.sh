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

OBSERVABILITY_COMPOSE_FILE="$PRODUCTION_DIR/observability.compose.yml"

compose_observability() {
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" -f "$OBSERVABILITY_COMPOSE_FILE" "$@"
}

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

require_command docker
require_command curl
require_production_variables

if [[ -z "${GRAFANA_ADMIN_PASSWORD_SECRET_FILE:-}" ]]; then
  echo "GRAFANA_ADMIN_PASSWORD_SECRET_FILE must be set to start observability." >&2
  exit 1
fi

grafana_secret_path="$(resolve_env_path "$GRAFANA_ADMIN_PASSWORD_SECRET_FILE")"
assert_secret_file "$grafana_secret_path" "Grafana admin password"

compose_observability config >/dev/null
compose_observability up -d otel-collector loki prometheus grafana

# Recreate only the API so the OTLP endpoint from the overlay is applied without
# restarting PostgreSQL or the public edge container.
compose_observability up -d --no-deps --force-recreate api

check_host="${OBSERVABILITY_CHECK_HOST:-127.0.0.1}"
wait_http "OpenTelemetry collector" "http://${check_host}:${OTEL_HEALTH_PORT:-13133}/"
wait_http "Loki" "http://${check_host}:${LOKI_PORT:-3100}/ready"
wait_http "Prometheus" "http://${check_host}:${PROMETHEUS_PORT:-9090}/-/ready"
wait_http "Grafana" "http://${check_host}:${GRAFANA_PORT:-3000}/api/health"
wait_for_app_ready 60

echo "Observability stack is healthy and the API is exporting OTLP telemetry."
echo "Grafana is bound to ${OBSERVABILITY_BIND:-127.0.0.1}:${GRAFANA_PORT:-3000}; use an SSH tunnel for remote access."
