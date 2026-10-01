#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --version <previous-version> [--env <production-env-file>]" >&2
}

target_version=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      target_version="${2:-}"
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

if [[ -z "$target_version" || ! "$target_version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "Rollback version must be numeric MAJOR.MINOR.PATCH." >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

require_command python3
require_command sha256sum
require_production_variables

update_root="$(resolve_env_path "$UPDATE_HOST_ROOT")"
archive_root="$update_root/releases/$target_version"
if [[ ! -d "$archive_root" ]]; then
  echo "Archived release does not exist: $archive_root" >&2
  exit 1
fi

for required in release.json publisher-certificate-sha256.txt publisher-certificate.cer; do
  if [[ ! -s "$archive_root/$required" ]]; then
    echo "Archived release is incomplete; missing $required in $archive_root" >&2
    exit 1
  fi
done

mapfile -t metadata < <(python3 - "$archive_root/release.json" "$target_version" <<'PY'
import json, re, sys
from pathlib import Path

manifest_path = Path(sys.argv[1])
expected_version = sys.argv[2]
data = json.loads(manifest_path.read_text(encoding="utf-8"))
if data.get("schemaVersion") != 1:
    raise SystemExit("Unsupported release manifest schemaVersion")
if data.get("channel") != "stable":
    raise SystemExit("Rollback target must be an archived stable release")
version = str(data.get("version", ""))
if version != expected_version:
    raise SystemExit("Archived release version does not match requested rollback target")
package = data.get("package") or {}
filename = str(package.get("file", ""))
sha256 = str(package.get("sha256", "")).upper()
size = package.get("sizeBytes")
if not filename or Path(filename).name != filename:
    raise SystemExit("Archived package.file must be a plain file name")
if not re.fullmatch(r"[0-9A-F]{64}", sha256):
    raise SystemExit("Archived package SHA-256 is invalid")
if not isinstance(size, int) or size <= 0:
    raise SystemExit("Archived package size is invalid")
print(filename)
print(sha256)
print(size)
PY
)

package_file="${metadata[0]}"
expected_sha="${metadata[1]}"
expected_size="${metadata[2]}"
package_path="$archive_root/$package_file"
if [[ ! -s "$package_path" ]]; then
  echo "Archived runtime package is missing: $package_path" >&2
  exit 1
fi
actual_size="$(wc -c < "$package_path" | tr -d ' ')"
actual_sha="$(sha256sum "$package_path" | awk '{print toupper($1)}')"
if [[ "$actual_size" != "$expected_size" || "$actual_sha" != "$expected_sha" ]]; then
  echo "Archived runtime package size/SHA-256 does not match release.json." >&2
  exit 1
fi

temp_bundle="$(mktemp -d)"
trap 'rm -rf "$temp_bundle"' EXIT
cp -f "$archive_root/release.json" "$temp_bundle/release.json"
cp -f "$package_path" "$temp_bundle/$package_file"
cp -f "$archive_root/publisher-certificate-sha256.txt" "$temp_bundle/publisher-certificate-sha256.txt"
cp -f "$archive_root/publisher-certificate.cer" "$temp_bundle/publisher-certificate.cer"

bash "$SCRIPT_DIR/publish-windows-release.sh" --env "$ENV_FILE" --bundle "$temp_bundle"
echo "Restored archived stable Windows release $target_version. Run acceptance-server.sh and verify the rollback in the Production Release Control Center before closing the rollback request."
