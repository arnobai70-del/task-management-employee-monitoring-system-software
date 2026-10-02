#!/usr/bin/env bash
set -euo pipefail
umask 077

usage() {
  cat >&2 <<'EOF'
Usage:
  bootstrap-admin.sh [--env <production-env-file>] [--email <admin-email>]

Creates the first production SuperAdmin through a one-shot migrator container.
The password is prompted securely and stored only in temporary Docker secret files
that are deleted when the command exits.
EOF
}

admin_email=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --env)
      export ENV_FILE="${2:-}"
      shift 2
      ;;
    --email)
      admin_email="${2:-}"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      usage
      exit 2
      ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

require_command docker
require_command mktemp
require_production_variables

if [[ -z "$admin_email" ]]; then
  read -r -p 'Bootstrap SuperAdmin email: ' admin_email
fi
admin_email="$(printf '%s' "$admin_email" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
if [[ -z "$admin_email" || "$admin_email" != *@*.* ]]; then
  echo 'A non-empty administrator email address is required.' >&2
  exit 1
fi

read -r -s -p 'Bootstrap SuperAdmin password (minimum 12 characters): ' admin_password
echo
if [[ ${#admin_password} -lt 12 ]]; then
  echo 'Bootstrap SuperAdmin password must contain at least 12 characters.' >&2
  unset admin_password
  exit 1
fi
read -r -s -p 'Confirm bootstrap SuperAdmin password: ' admin_password_confirmation
echo
if [[ "$admin_password" != "$admin_password_confirmation" ]]; then
  echo 'Bootstrap SuperAdmin passwords do not match.' >&2
  unset admin_password admin_password_confirmation
  exit 1
fi
unset admin_password_confirmation

work_dir="$(mktemp -d)"
cleanup() {
  unset admin_password
  rm -rf "$work_dir"
}
trap cleanup EXIT INT TERM

email_secret="$work_dir/bootstrap_admin_email"
password_secret="$work_dir/bootstrap_admin_password"
override_file="$work_dir/bootstrap.override.yml"
printf '%s' "$admin_email" > "$email_secret"
printf '%s' "$admin_password" > "$password_secret"
unset admin_password
chmod 600 "$email_secret" "$password_secret"

cat > "$override_file" <<EOF
services:
  migrator:
    secrets:
      - source: bootstrap_admin_email
        target: BootstrapAdmin__Email
      - source: bootstrap_admin_password
        target: BootstrapAdmin__Password
secrets:
  bootstrap_admin_email:
    file: $email_secret
  bootstrap_admin_password:
    file: $password_secret
EOF
chmod 600 "$override_file"

compose up -d postgres
wait_for_postgres 60

echo "Creating production SuperAdmin '$admin_email' through a one-shot bootstrap container..."
docker compose \
  --env-file "$ENV_FILE" \
  -f "$COMPOSE_FILE" \
  -f "$override_file" \
  run --rm migrator --migrate-only

superadmin_count="$(compose exec -T postgres psql \
  -U "$POSTGRES_USER" \
  -d "$POSTGRES_DB" \
  -v ON_ERROR_STOP=1 \
  -tAc "SELECT count(*) FROM users u JOIN user_roles ur ON ur.\"UserId\" = u.\"Id\" JOIN roles r ON r.\"Id\" = ur.\"RoleId\" WHERE u.\"IsActive\" = true AND r.\"Name\" = 'SuperAdmin';" | tr -d '[:space:]')"

if [[ ! "$superadmin_count" =~ ^[0-9]+$ || "$superadmin_count" -lt 1 ]]; then
  echo 'Bootstrap command completed but no active SuperAdmin account was found. Do not proceed with production launch.' >&2
  exit 1
fi

echo "Production SuperAdmin bootstrap verified. Active SuperAdmin accounts: $superadmin_count"
echo 'Temporary bootstrap password/email secret files have been scheduled for deletion on exit.'
