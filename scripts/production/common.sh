#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PRODUCTION_DIR="$REPO_ROOT/deploy/production"
ENV_FILE="${ENV_FILE:-$PRODUCTION_DIR/production.env}"
COMPOSE_FILE="$PRODUCTION_DIR/docker-compose.yml"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "Production environment file not found: $ENV_FILE" >&2
  exit 1
fi

ENV_FILE="$(cd "$(dirname "$ENV_FILE")" && pwd)/$(basename "$ENV_FILE")"
ENV_DIR="$(dirname "$ENV_FILE")"

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

require_command() {
  local command_name="$1"
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command is not installed: $command_name" >&2
    exit 1
  fi
}

resolve_env_path() {
  local value="$1"
  if [[ "$value" = /* ]]; then
    printf '%s\n' "$value"
  else
    printf '%s\n' "$ENV_DIR/${value#./}"
  fi
}

compose() {
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
}

require_production_variables() {
  local required=(
    APP_SITE_ADDRESS
    UPDATE_SITE_ADDRESS
    ACME_EMAIL
    APP_PUBLIC_URL
    UPDATE_PUBLIC_URL
    POSTGRES_DB
    POSTGRES_USER
    POSTGRES_PASSWORD_SECRET_FILE
    DATABASE_CONNECTION_SECRET_FILE
    JWT_SIGNING_KEY_SECRET_FILE
    UPDATE_HOST_ROOT
    BACKUP_ROOT
  )

  local missing=0
  for name in "${required[@]}"; do
    if [[ -z "${!name:-}" ]]; then
      echo "Required production variable is empty: $name" >&2
      missing=1
    fi
  done
  if [[ "$missing" -ne 0 ]]; then
    exit 1
  fi
}

wait_for_postgres() {
  local attempts="${1:-60}"
  for ((i = 1; i <= attempts; i++)); do
    if compose exec -T postgres pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done

  echo "PostgreSQL did not become ready." >&2
  return 1
}

wait_for_app_ready() {
  local attempts="${1:-60}"
  local url="${APP_PUBLIC_URL%/}/health/ready"
  for ((i = 1; i <= attempts; i++)); do
    if curl --fail --silent --show-error --max-time 10 "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done

  echo "Application readiness endpoint did not become healthy: $url" >&2
  return 1
}

assert_secret_file() {
  local path="$1"
  local label="$2"
  if [[ ! -f "$path" || ! -s "$path" ]]; then
    echo "$label secret file is missing or empty: $path" >&2
    exit 1
  fi
}
