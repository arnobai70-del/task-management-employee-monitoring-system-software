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

require_command mktemp
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

certificate_sha="$(sha256sum "$certificate_path" | awk '{print toupper($1)}')"
if [[ "$certificate_sha" != "$fingerprint" ]]; then
  echo "Publisher certificate bytes do not match publisher-certificate-sha256.txt." >&2
  exit 1
fi

update_root="$(resolve_env_path "$UPDATE_HOST_ROOT")"
channel_root="$update_root/$channel"
releases_root="$update_root/releases"
archive_root="$releases_root/$version"
mkdir -p "$channel_root" "$releases_root"

incoming_manifest_sha="$(sha256sum "$manifest_path" | awk '{print toupper($1)}')"
if [[ -e "$archive_root" ]]; then
  if [[ ! -d "$archive_root" ]]; then
    echo "Release archive path already exists and is not a directory: $archive_root" >&2
    exit 1
  fi

  existing_manifest="$archive_root/release.json"
  existing_package="$archive_root/$package_file"
  existing_fingerprint_file="$archive_root/publisher-certificate-sha256.txt"
  existing_certificate="$archive_root/publisher-certificate.cer"
  for existing in "$existing_manifest" "$existing_package" "$existing_fingerprint_file" "$existing_certificate"; do
    if [[ ! -s "$existing" ]]; then
      echo "Release version $version already has an incomplete archive. Refusing to overwrite it: $archive_root" >&2
      exit 1
    fi
  done

  existing_manifest_sha="$(sha256sum "$existing_manifest" | awk '{print toupper($1)}')"
  existing_package_sha="$(sha256sum "$existing_package" | awk '{print toupper($1)}')"
  existing_package_size="$(wc -c < "$existing_package" | tr -d ' ')"
  existing_fingerprint="$(tr -d '[:space:]' < "$existing_fingerprint_file" | tr '[:lower:]' '[:upper:]')"
  existing_certificate_sha="$(sha256sum "$existing_certificate" | awk '{print toupper($1)}')"

  if [[ "$existing_manifest_sha" != "$incoming_manifest_sha" ||
        "$existing_package_sha" != "$expected_sha" ||
        "$existing_package_size" != "$expected_size" ||
        "$existing_fingerprint" != "$fingerprint" ||
        "$existing_certificate_sha" != "$fingerprint" ]]; then
    echo "Release version $version is already archived with different bytes. Published versions are immutable; build a new version instead of overwriting the rollback archive." >&2
    exit 1
  fi

  echo "Release archive $version already contains the identical signed bundle; keeping the immutable archive unchanged."
else
  archive_tmp="$(mktemp -d "$releases_root/.${version}.publish.XXXXXX")"
  cleanup_archive_tmp() {
    if [[ -n "${archive_tmp:-}" && -d "$archive_tmp" ]]; then
      rm -rf "$archive_tmp"
    fi
  }
  trap cleanup_archive_tmp EXIT

  cp "$package_path" "$archive_tmp/$package_file"
  cp "$manifest_path" "$archive_tmp/release.json"
  cp "$fingerprint_path" "$archive_tmp/publisher-certificate-sha256.txt"
  cp "$certificate_path" "$archive_tmp/publisher-certificate.cer"
  chmod 0644 "$archive_tmp/$package_file" "$archive_tmp/release.json" "$archive_tmp/publisher-certificate-sha256.txt" "$archive_tmp/publisher-certificate.cer"

  # GNU mv -T performs an atomic directory rename and refuses to merge into an
  # archive that another publisher created concurrently.
  mv -T "$archive_tmp" "$archive_root"
  archive_tmp=""
  trap - EXIT
fi

cp -f "$package_path" "$channel_root/$package_file.tmp"
mv -f "$channel_root/$package_file.tmp" "$channel_root/$package_file"
cp -f "$manifest_path" "$channel_root/release.json.tmp"
mv -f "$channel_root/release.json.tmp" "$channel_root/release.json"
chmod 0644 "$channel_root/$package_file" "$channel_root/release.json"

echo "Published Windows release $version to ${UPDATE_PUBLIC_URL%/}/$channel/release.json"
echo "Publisher certificate SHA-256: $fingerprint"
