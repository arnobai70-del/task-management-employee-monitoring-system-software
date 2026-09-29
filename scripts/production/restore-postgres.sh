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

cat "$backup_path" | compose exec -T postgres pg_restore --list >/dev/null

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

cat "$backup_path" | compose exec -T postgres sh -ec '
  export PGPASSWORD="$(cat /run/secrets/postgres_password)"
  exec pg_restore \
    --host=127.0.0.1 \
    --username="$POSTGRES_USER" \
    --dbname="$POSTGRES_DB" \
    --clean \
    --if-exists \
    --no-owner \
    --no-acl \
    --exit-on-error
'

restart_api
trap - EXIT

echo "PostgreSQL restore completed from: $backup_path"
