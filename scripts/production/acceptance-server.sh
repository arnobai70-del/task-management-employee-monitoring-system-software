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
require_production_variables

app_root="${APP_PUBLIC_URL%/}"
update_root="${UPDATE_PUBLIC_URL%/}"
manifest_url="$update_root/$channel/release.json"

temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT

curl --fail --silent --show-error --max-time 15 "$app_root/health/live" > "$temp_dir/live.txt"
curl --fail --silent --show-error --max-time 15 "$app_root/health/ready" > "$temp_dir/ready.txt"
curl --fail --silent --show-error --max-time 15 "$app_root/" > "$temp_dir/index.html"
if ! grep -qi '<html' "$temp_dir/index.html"; then
  echo "Admin Web root did not return HTML." >&2
  exit 1
fi

curl --fail --silent --show-error --max-time 30 "$manifest_url" > "$temp_dir/release.json"
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

echo "Production acceptance passed: app ready, Admin Web reachable, $channel release $version downloadable and hash-valid."
