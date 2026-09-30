#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --bundle <signed-release-directory> [--env <production-env-file>]" >&2
}

bundle_dir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --bundle)
      bundle_dir="${2:-}"
      shift 2
      ;;
    --env)
      export ENV_FILE="${2:-}"
      shift 2
      ;;
    *)
      usage
      exit 2
      ;;
  esac
done

if [[ -z "$bundle_dir" ]]; then
  usage
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

require_command python3
require_command sha256sum
require_production_variables

bundle_dir="$(cd "$bundle_dir" && pwd)"
manifest_path="$bundle_dir/release.json"
fingerprint_path="$bundle_dir/publisher-certificate-sha256.txt"
certificate_path="$bundle_dir/publisher-certificate.cer"

if [[ ! -s "$manifest_path" || ! -s "$fingerprint_path" || ! -s "$certificate_path" ]]; then
  echo "Signed bundle must contain release.json, publisher-certificate-sha256.txt and publisher-certificate.cer." >&2
  exit 1
fi

mapfile -t metadata < <(python3 - "$manifest_path" "${UPDATE_PUBLIC_URL%/}" <<'PY'
import json, re, sys
from pathlib import Path

manifest_path = Path(sys.argv[1])
public_url = sys.argv[2]
data = json.loads(manifest_path.read_text(encoding="utf-8"))
if data.get("schemaVersion") != 1:
    raise SystemExit("Unsupported release manifest schemaVersion")
channel = str(data.get("channel", ""))
version = str(data.get("version", ""))
package = data.get("package") or {}
filename = str(package.get("file", ""))
sha256 = str(package.get("sha256", "")).upper()
size = package.get("sizeBytes")
url = package.get("url")
if channel not in {"stable", "beta"}:
    raise SystemExit("Manifest channel must be stable or beta")
if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
    raise SystemExit("Manifest version must be numeric MAJOR.MINOR.PATCH")
if not filename or Path(filename).name != filename:
    raise SystemExit("Manifest package.file must be a plain file name")
if not re.fullmatch(r"[0-9A-Fa-f]{64}", sha256):
    raise SystemExit("Manifest package.sha256 is invalid")
if not isinstance(size, int) or size <= 0:
    raise SystemExit("Manifest package.sizeBytes is invalid")
expected_url = f"{public_url}/{channel}/{filename}"
if url not in (None, "", expected_url):
    raise SystemExit(f"Manifest package.url must be empty or {expected_url}")
print(channel)
print(version)
print(filename)
print(sha256)
print(size)
PY
)

channel="${metadata[0]}"
version="${metadata[1]}"
package_file="${metadata[2]}"
expected_sha="${metadata[3]}"
expected_size="${metadata[4]}"
package_path="$bundle_dir/$package_file"

if [[ ! -s "$package_path" ]]; then
  echo "Runtime package is missing: $package_path" >&2
  exit 1
fi

actual_size="$(wc -c < "$package_path" | tr -d ' ')"
actual_sha="$(sha256sum "$package_path" | awk '{print toupper($1)}')"
if [[ "$actual_size" != "$expected_size" || "$actual_sha" != "$expected_sha" ]]; then
  echo "Runtime package size/SHA-256 does not match release.json." >&2
  exit 1
fi

fingerprint="$(tr -d '[:space:]' < "$fingerprint_path" | tr '[:lower:]' '[:upper:]')"
if [[ ! "$fingerprint" =~ ^[0-9A-F]{64}$ ]]; then
  echo "Publisher certificate SHA-256 fingerprint is invalid." >&2
  exit 1
fi

update_root="$(resolve_env_path "$UPDATE_HOST_ROOT")"
channel_root="$update_root/$channel"
archive_root="$update_root/releases/$version"
mkdir -p "$channel_root" "$archive_root"

cp -f "$package_path" "$archive_root/$package_file"
cp -f "$manifest_path" "$archive_root/release.json"
cp -f "$fingerprint_path" "$archive_root/publisher-certificate-sha256.txt"
cp -f "$certificate_path" "$archive_root/publisher-certificate.cer"

cp -f "$package_path" "$channel_root/$package_file.tmp"
mv -f "$channel_root/$package_file.tmp" "$channel_root/$package_file"
cp -f "$manifest_path" "$channel_root/release.json.tmp"
mv -f "$channel_root/release.json.tmp" "$channel_root/release.json"
chmod 0644 "$channel_root/$package_file" "$channel_root/release.json"

echo "Published Windows release $version to ${UPDATE_PUBLIC_URL%/}/$channel/release.json"
echo "Publisher certificate SHA-256: $fingerprint"
