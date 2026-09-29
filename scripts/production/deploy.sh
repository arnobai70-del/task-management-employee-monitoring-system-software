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

require_command docker
require_command curl
require_production_variables

postgres_password_path="$(resolve_env_path "$POSTGRES_PASSWORD_SECRET_FILE")"
database_connection_path="$(resolve_env_path "$DATABASE_CONNECTION_SECRET_FILE")"
jwt_signing_key_path="$(resolve_env_path "$JWT_SIGNING_KEY_SECRET_FILE")"
update_host_root="$(resolve_env_path "$UPDATE_HOST_ROOT")"

assert_secret_file "$postgres_password_path" "PostgreSQL password"
assert_secret_file "$database_connection_path" "Database connection"
assert_secret_file "$jwt_signing_key_path" "JWT signing key"
mkdir -p "$update_host_root"

compose config >/dev/null
compose build api web
compose up -d postgres
wait_for_postgres 60

backup_path="$($SCRIPT_DIR/backup-postgres.sh "$ENV_FILE" predeploy)"
echo "Pre-deployment database backup: $backup_path"

compose --profile tools run --rm migrator
compose up -d api web
wait_for_app_ready 60

curl --fail --silent --show-error --max-time 10 "${APP_PUBLIC_URL%/}/health/live" >/dev/null
curl --fail --silent --show-error --max-time 10 "${APP_PUBLIC_URL%/}/health/ready" >/dev/null

echo "Production deployment is healthy: ${APP_PUBLIC_URL%/}"
