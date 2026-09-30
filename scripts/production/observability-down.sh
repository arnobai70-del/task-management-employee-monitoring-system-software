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

require_command docker
require_command curl
require_production_variables

compose_observability stop grafana prometheus loki otel-collector || true
compose_observability rm -f grafana prometheus loki otel-collector || true

# Recreate the API from the base production model so it no longer targets the
# stopped collector. Metrics/log export is optional and normal API behavior remains.
compose up -d --no-deps --force-recreate api
wait_for_app_ready 60

echo "Observability containers stopped. Metrics/log volumes were preserved."
echo "The API was recreated from the base production configuration and remains healthy."
