#!/usr/bin/env bash
set -euo pipefail

channel="stable"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --env)
      export ENV_FILE="${2:-}"
      shift 2
      ;;
    --channel)
      channel="${2:-}"
      shift 2
      ;;
    *)
      echo "Usage: $0 [--env <production-env-file>] [--channel stable|beta]" >&2
      exit 2
      ;;
  esac
done

if [[ "$channel" != "stable" && "$channel" != "beta" ]]; then
  echo "Channel must be stable or beta." >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

require_command curl
require_command python3
require_command sha256sum
require_command stat
require_production_variables

app_root="${APP_PUBLIC_URL%/}"
update_root="${UPDATE_PUBLIC_URL%/}"
manifest_url="$update_root/$channel/release.json"

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

curl --fail --silent --show-error --max-time 15 "$app_root/health/live" > "$temp_dir/live.txt"
curl --fail --silent --show-error --max-time 15 "$app_root/health/ready" > "$temp_dir/ready.txt"
curl --fail --silent --show-error --max-time 15 --dump-header "$temp_dir/index.headers" "$app_root/" > "$temp_dir/index.html"
if ! grep -qi '<html' "$temp_dir/index.html"; then
  echo "Admin Web root did not return HTML." >&2
  exit 1
fi

for header in \
  'content-security-policy:' \
  'strict-transport-security:' \
  'x-content-type-options: nosniff' \
  'x-frame-options: deny' \
  'referrer-policy: strict-origin-when-cross-origin' \
  'permissions-policy:'; do
  if ! grep -qi "^$header" "$temp_dir/index.headers"; then
    echo "Admin Web security header missing: $header" >&2
    exit 1
  fi
done

openapi_status="$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 15 "$app_root/openapi/v1.json")"
if [[ "$openapi_status" != "401" && "$openapi_status" != "404" ]]; then
  echo "Production OpenAPI endpoint must not be anonymously exposed; received HTTP $openapi_status." >&2
  exit 1
fi

for secret_path in \
  "$(resolve_env_path "$POSTGRES_PASSWORD_SECRET_FILE")" \
  "$(resolve_env_path "$DATABASE_CONNECTION_SECRET_FILE")" \
  "$(resolve_env_path "$JWT_SIGNING_KEY_SECRET_FILE")" \
  "$(resolve_env_path "$AGENT_UPDATE_ENROLLMENT_KEY_SECRET_FILE")"; do
  if [[ -L "$secret_path" ]]; then
    echo "Production secret must not be a symbolic link: $secret_path" >&2
    exit 1
  fi
  mode="$(stat -c '%a' "$secret_path")"
  if [[ "$mode" != "600" ]]; then
    echo "Production secret must use mode 600: $secret_path has $mode" >&2
    exit 1
  fi
done

curl --fail --silent --show-error --max-time 30 --dump-header "$temp_dir/release.headers" "$manifest_url" > "$temp_dir/release.json"
if ! grep -qi '^cache-control: .*no-store' "$temp_dir/release.headers"; then
  echo "Published release manifest must be served with no-store cache control." >&2
  exit 1
fi
if ! grep -qi '^x-content-type-options: nosniff' "$temp_dir/release.headers"; then
  echo "Update host is missing X-Content-Type-Options: nosniff." >&2
  exit 1
fi

mapfile -t metadata < <(python3 - "$temp_dir/release.json" "$manifest_url" <<'PY'
import json, re, sys
from urllib.parse import urljoin

path, manifest_url = sys.argv[1:]
data = json.load(open(path, encoding="utf-8"))
if data.get("schemaVersion") != 1:
    raise SystemExit("Unsupported manifest schema")
version = str(data.get("version", ""))
if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
    raise SystemExit("Invalid manifest version")
package = data.get("package") or {}
filename = str(package.get("file", ""))
sha256 = str(package.get("sha256", "")).upper()
size = package.get("sizeBytes")
if not filename or not re.fullmatch(r"[0-9A-Fa-f]{64}", sha256) or not isinstance(size, int) or size <= 0:
    raise SystemExit("Invalid package metadata")
url = package.get("url") or urljoin(manifest_url, filename)
print(version)
print(url)
print(sha256)
print(size)
PY
)

version="${metadata[0]}"
package_url="${metadata[1]}"
expected_sha="${metadata[2]}"
expected_size="${metadata[3]}"
package_path="$temp_dir/runtime.zip"

curl --fail --silent --show-error --max-time 120 "$package_url" > "$package_path"
actual_size="$(wc -c < "$package_path" | tr -d ' ')"
actual_sha="$(sha256sum "$package_path" | awk '{print toupper($1)}')"
if [[ "$actual_size" != "$expected_size" || "$actual_sha" != "$expected_sha" ]]; then
  echo "Published runtime package failed size/SHA-256 acceptance." >&2
  exit 1
fi

echo "Production acceptance passed: app ready, security headers enforced, OpenAPI hidden, secrets private, and $channel release $version hash-valid."
