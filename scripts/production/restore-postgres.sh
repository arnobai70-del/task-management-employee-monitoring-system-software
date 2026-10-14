#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --backup <archive.dump> --confirm-restore [--env <production-env-file>]" >&2
}

backup_path=""
confirmed=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --backup)
      backup_path="${2:-}"
      shift 2
      ;;
    --env)
      export ENV_FILE="${2:-}"
      shift 2
      ;;
    --confirm-restore)
      confirmed=1
      shift
      ;;
    *)
      usage
      exit 2
      ;;
  esac
done

if [[ -z "$backup_path" || "$confirmed" -ne 1 ]]; then
  usage
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

require_command docker
require_command sha256sum
require_production_variables

backup_path="$(cd "$(dirname "$backup_path")" && pwd)/$(basename "$backup_path")"
if [[ ! -s "$backup_path" ]]; then
  echo "Backup archive is missing or empty: $backup_path" >&2
  exit 1
fi

checksum_path="$backup_path.sha256"
if [[ -f "$checksum_path" ]]; then
  (cd "$(dirname "$backup_path")" && sha256sum --check "$(basename "$checksum_path")")
else
  echo "Warning: no checksum sidecar found for $backup_path" >&2
fi

# Read the whole archive before stopping the API, so a damaged file is rejected without downtime.
# Redirect, not `cat |`, for the same SIGPIPE reason as in backup-postgres.sh (G-50).
if ! compose exec -T postgres pg_restore --file=/dev/null < "$backup_path"; then
  echo "Backup archive could not be read: $backup_path" >&2
  exit 1
fi

api_was_running=0
if compose ps --status running --services | grep -qx 'api'; then
  api_was_running=1
  compose stop api >/dev/null
fi

restart_api() {
  if [[ "$api_was_running" -eq 1 ]]; then
    compose start api >/dev/null || true
  fi
}
trap restart_api EXIT

# The whole restore is one transaction that first replaces the public schema. `pg_restore --clean`
# only drops objects that are in the archive, so rolling back a release that added tables failed on
# their foreign keys and left the database with most constraints dropped (G-49). COMMIT is sent only
# after pg_restore has produced the complete script; on any error psql stops, the transaction is
# rolled back, and the database is exactly as it was before the restore.
compose exec -T postgres sh -ec '
  set -o pipefail
  export PGPASSWORD="$(cat /run/secrets/postgres_password)"
  export PGOPTIONS="-c client_min_messages=warning"
  {
    printf "BEGIN;\nDROP SCHEMA IF EXISTS public CASCADE;\nCREATE SCHEMA public;\n"
    pg_restore --no-owner --no-acl --exit-on-error --file=- || exit 1
    printf "COMMIT;\n"
  } | psql \
    --host=127.0.0.1 \
    --username="$POSTGRES_USER" \
    --dbname="$POSTGRES_DB" \
    --no-psqlrc \
    --quiet \
    --output=/dev/null \
    -v ON_ERROR_STOP=1
' < "$backup_path"

restart_api
trap - EXIT

echo "PostgreSQL restore completed from: $backup_path"
