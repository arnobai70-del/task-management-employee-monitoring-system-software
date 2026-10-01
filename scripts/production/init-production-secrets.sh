#!/usr/bin/env bash
set -euo pipefail
umask 077

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

require_command openssl
require_production_variables

postgres_password_path="$(resolve_env_path "$POSTGRES_PASSWORD_SECRET_FILE")"
database_connection_path="$(resolve_env_path "$DATABASE_CONNECTION_SECRET_FILE")"
jwt_signing_key_path="$(resolve_env_path "$JWT_SIGNING_KEY_SECRET_FILE")"
agent_update_enrollment_key_path="$(resolve_env_path "$AGENT_UPDATE_ENROLLMENT_KEY_SECRET_FILE")"

for path in "$postgres_password_path" "$database_connection_path" "$jwt_signing_key_path" "$agent_update_enrollment_key_path"; do
  mkdir -p "$(dirname "$path")"
  chmod 700 "$(dirname "$path")"
  if [[ -L "$path" ]]; then
    echo "Refusing symbolic-link secret path: $path" >&2
    exit 1
  fi
done

if [[ ! -e "$postgres_password_path" ]]; then
  openssl rand -hex 32 > "$postgres_password_path"
  echo "Created PostgreSQL password secret."
fi

if [[ ! -e "$jwt_signing_key_path" ]]; then
  openssl rand -hex 64 > "$jwt_signing_key_path"
  echo "Created JWT signing-key secret."
fi

if [[ ! -e "$agent_update_enrollment_key_path" ]]; then
  openssl rand -hex 32 > "$agent_update_enrollment_key_path"
  echo "Created centralized agent-update enrollment secret."
fi

assert_secret_file "$postgres_password_path" "PostgreSQL password"
postgres_password="$(tr -d '\r\n' < "$postgres_password_path")"
if [[ -z "$postgres_password" ]]; then
  echo "PostgreSQL password secret is empty." >&2
  exit 1
fi

if [[ ! -e "$database_connection_path" ]]; then
  printf 'Host=postgres;Port=5432;Database=%s;Username=%s;Password=%s;Pooling=true;Timeout=15;Command Timeout=30\n' \
    "$POSTGRES_DB" "$POSTGRES_USER" "$postgres_password" > "$database_connection_path"
  echo "Created API database-connection secret."
fi

assert_secret_file "$postgres_password_path" "PostgreSQL password"
assert_secret_file "$database_connection_path" "Database connection"
assert_secret_file "$jwt_signing_key_path" "JWT signing key"
assert_secret_file "$agent_update_enrollment_key_path" "Agent update enrollment key"

for path in "$postgres_password_path" "$database_connection_path" "$jwt_signing_key_path" "$agent_update_enrollment_key_path"; do
  if [[ -L "$path" ]]; then
    echo "Refusing symbolic-link secret path: $path" >&2
    exit 1
  fi
  chmod 600 "$path"
done

jwt_bytes="$(tr -d '\r\n' < "$jwt_signing_key_path" | wc -c | tr -d ' ')"
if [[ "$jwt_bytes" -lt 32 ]]; then
  echo "JWT signing key must contain at least 32 bytes." >&2
  exit 1
fi

enrollment_bytes="$(tr -d '\r\n' < "$agent_update_enrollment_key_path" | wc -c | tr -d ' ')"
if [[ "$enrollment_bytes" -lt 32 ]]; then
  echo "Agent update enrollment key must contain at least 32 bytes." >&2
  exit 1
fi

echo "Production secret files are present with generated values only where files were missing."
echo "Secret files are regular files with owner-only read/write permissions."
echo "Back up these secrets in your organization secret manager before deployment."
