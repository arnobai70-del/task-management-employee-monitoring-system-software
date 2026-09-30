#!/usr/bin/env bash
set -euo pipefail

if [[ $# -gt 2 ]]; then
  echo "Usage: $0 [production-env-file] [label]" >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ $# -ge 1 && -n "${1:-}" ]]; then
  export ENV_FILE="$1"
fi
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

require_command docker
require_command sha256sum
require_production_variables

label="${2:-manual}"
if [[ ! "$label" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "Backup label may contain only letters, digits, dot, underscore and dash." >&2
  exit 2
fi

backup_root="$(resolve_env_path "$BACKUP_ROOT")"
mkdir -p "$backup_root"
chmod 700 "$backup_root"

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
backup_path="$backup_root/taskmonitoring-${timestamp}-${label}.dump"
temporary_path="${backup_path}.partial"

rm -f "$temporary_path"
compose exec -T postgres sh -ec '
  export PGPASSWORD="$(cat /run/secrets/postgres_password)"
  exec pg_dump \
    --host=127.0.0.1 \
    --username="$POSTGRES_USER" \
    --dbname="$POSTGRES_DB" \
    --format=custom \
    --compress=9 \
    --no-owner \
    --no-acl
' > "$temporary_path"

if [[ ! -s "$temporary_path" ]]; then
  echo "Backup archive is empty." >&2
  rm -f "$temporary_path"
  exit 1
fi

cat "$temporary_path" | compose exec -T postgres pg_restore --list >/dev/null
mv "$temporary_path" "$backup_path"
(
  cd "$backup_root"
  sha256sum "$(basename "$backup_path")" > "$(basename "$backup_path").sha256.tmp"
  mv "$(basename "$backup_path").sha256.tmp" "$(basename "$backup_path").sha256"
)
chmod 600 "$backup_path" "$backup_path.sha256"

printf '%s\n' "$backup_path"
