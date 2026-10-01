#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat >&2 <<'EOF'
Usage:
  accept-real-production-launch.sh \
    --env <production-env-file> \
    --release-bundle <signed-release-directory> \
    --expected-publisher-sha256 <64-hex> \
    --primary-backup <production-backup.dump> \
    --offsite-backup <independent-storage-copy.dump> \
    --offsite-storage-reference <ticket/policy/reference> \
    --pilot-evidence <production-pilot-evidence.json> \
    --signed-workflow-reference <workflow-run/reference> \
    --business-approval-reference <approval/reference> \
    --security-approval-reference <approval/reference> \
    --output <launch-acceptance.json>
EOF
}

release_bundle=""
expected_publisher=""
primary_backup=""
offsite_backup=""
offsite_reference=""
pilot_evidence=""
signed_workflow_reference=""
business_approval_reference=""
security_approval_reference=""
output_path=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --env) export ENV_FILE="${2:-}"; shift 2 ;;
    --release-bundle) release_bundle="${2:-}"; shift 2 ;;
    --expected-publisher-sha256) expected_publisher="${2:-}"; shift 2 ;;
    --primary-backup) primary_backup="${2:-}"; shift 2 ;;
    --offsite-backup) offsite_backup="${2:-}"; shift 2 ;;
    --offsite-storage-reference) offsite_reference="${2:-}"; shift 2 ;;
    --pilot-evidence) pilot_evidence="${2:-}"; shift 2 ;;
    --signed-workflow-reference) signed_workflow_reference="${2:-}"; shift 2 ;;
    --business-approval-reference) business_approval_reference="${2:-}"; shift 2 ;;
    --security-approval-reference) security_approval_reference="${2:-}"; shift 2 ;;
    --output) output_path="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

for required in release_bundle expected_publisher primary_backup offsite_backup offsite_reference pilot_evidence signed_workflow_reference business_approval_reference security_approval_reference output_path; do
  if [[ -z "${!required}" ]]; then
    echo "Missing required argument: $required" >&2
    usage
    exit 2
  fi
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$SCRIPT_DIR/common.sh"

require_command curl
require_command getent
require_command python3
require_command realpath
require_command sha256sum
require_command stat
require_production_variables

expected_publisher="$(printf '%s' "$expected_publisher" | tr -d '[:space:]' | tr '[:lower:]' '[:upper:]')"
if [[ ! "$expected_publisher" =~ ^[0-9A-F]{64}$ ]]; then
  echo "Expected publisher SHA-256 must contain exactly 64 hexadecimal characters." >&2
  exit 1
fi

release_bundle="$(realpath "$release_bundle")"
primary_backup="$(realpath "$primary_backup")"
offsite_backup="$(realpath "$offsite_backup")"
pilot_evidence="$(realpath "$pilot_evidence")"
output_parent="$(dirname "$output_path")"
mkdir -p "$output_parent"
output_path="$(cd "$output_parent" && pwd)/$(basename "$output_path")"

for file in "$release_bundle/release.json" "$release_bundle/publisher-certificate-sha256.txt" "$release_bundle/publisher-certificate.cer" "$primary_backup" "$offsite_backup" "$pilot_evidence"; do
  if [[ ! -s "$file" ]]; then
    echo "Required production acceptance evidence is missing or empty: $file" >&2
    exit 1
  fi
done

mapfile -t url_metadata < <(python3 - "$APP_PUBLIC_URL" "$UPDATE_PUBLIC_URL" <<'PY'
import ipaddress, sys
from urllib.parse import urlparse

for raw in sys.argv[1:]:
    parsed = urlparse(raw)
    if parsed.scheme.lower() != 'https' or not parsed.hostname:
        raise SystemExit(f"Production URL must use trusted HTTPS: {raw}")
    host = parsed.hostname.lower().rstrip('.')
    if host in {'localhost'} or host.endswith('.localhost'):
        raise SystemExit(f"Production URL must not use localhost: {raw}")
    try:
        ip = ipaddress.ip_address(host)
        if ip.is_loopback or ip.is_private or ip.is_link_local:
            raise SystemExit(f"Production URL must use a publicly reachable DNS name: {raw}")
    except ValueError:
        pass
    print(host)
PY
)

app_host="${url_metadata[0]}"
update_host="${url_metadata[1]}"
getent ahosts "$app_host" >/dev/null || { echo "Production application DNS does not resolve: $app_host" >&2; exit 1; }
getent ahosts "$update_host" >/dev/null || { echo "Production update DNS does not resolve: $update_host" >&2; exit 1; }

# curl performs normal CA/hostname verification; no insecure TLS override is used.
curl --fail --silent --show-error --max-time 20 "${APP_PUBLIC_URL%/}/health/ready" >/dev/null
curl --fail --silent --show-error --max-time 30 "${UPDATE_PUBLIC_URL%/}/stable/release.json" >/dev/null

# Reuse the full production acceptance suite for headers, readiness, secrets and published package hash.
bash "$SCRIPT_DIR/acceptance-server.sh" --env "$ENV_FILE" --channel stable >/dev/null

mapfile -t release_metadata < <(python3 - "$release_bundle/release.json" <<'PY'
import hashlib, json, re, sys
from pathlib import Path
path = Path(sys.argv[1])
data = json.loads(path.read_text(encoding='utf-8'))
if data.get('schemaVersion') != 1 or data.get('channel') != 'stable':
    raise SystemExit('Signed bundle must contain a schemaVersion 1 stable release manifest.')
version = str(data.get('version', ''))
if not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', version):
    raise SystemExit('Signed bundle version is invalid.')
package = data.get('package') or {}
filename = str(package.get('file', ''))
sha = str(package.get('sha256', '')).upper()
size = package.get('sizeBytes')
if not filename or Path(filename).name != filename or not re.fullmatch(r'[0-9A-F]{64}', sha) or not isinstance(size, int) or size <= 0:
    raise SystemExit('Signed bundle package metadata is invalid.')
print(version)
print(filename)
print(sha)
print(size)
print(hashlib.sha256(path.read_bytes()).hexdigest().upper())
PY
)
version="${release_metadata[0]}"
package_file="${release_metadata[1]}"
expected_package_sha="${release_metadata[2]}"
expected_package_size="${release_metadata[3]}"
expected_manifest_sha="${release_metadata[4]}"
package_path="$release_bundle/$package_file"
if [[ ! -s "$package_path" ]]; then
  echo "Signed runtime package is missing: $package_path" >&2
  exit 1
fi
actual_package_sha="$(sha256sum "$package_path" | awk '{print toupper($1)}')"
actual_package_size="$(wc -c < "$package_path" | tr -d ' ')"
if [[ "$actual_package_sha" != "$expected_package_sha" || "$actual_package_size" != "$expected_package_size" ]]; then
  echo "Signed release bundle package does not match release.json." >&2
  exit 1
fi

bundle_publisher="$(tr -d '[:space:]' < "$release_bundle/publisher-certificate-sha256.txt" | tr '[:lower:]' '[:upper:]')"
if [[ "$bundle_publisher" != "$expected_publisher" ]]; then
  echo "Signed release publisher fingerprint does not match the independently verified fingerprint." >&2
  exit 1
fi

# The exact published manifest and package must match the independently validated signed bundle.
tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT
curl --fail --silent --show-error --max-time 30 "${UPDATE_PUBLIC_URL%/}/stable/release.json" > "$tmpdir/live-release.json"
live_manifest_sha="$(sha256sum "$tmpdir/live-release.json" | awk '{print toupper($1)}')"
if [[ "$live_manifest_sha" != "$expected_manifest_sha" ]]; then
  echo "Live stable release manifest bytes differ from the validated signed bundle." >&2
  exit 1
fi
curl --fail --silent --show-error --max-time 180 "${UPDATE_PUBLIC_URL%/}/stable/$package_file" > "$tmpdir/live-package.zip"
live_package_sha="$(sha256sum "$tmpdir/live-package.zip" | awk '{print toupper($1)}')"
live_package_size="$(wc -c < "$tmpdir/live-package.zip" | tr -d ' ')"
if [[ "$live_package_sha" != "$expected_package_sha" || "$live_package_size" != "$expected_package_size" ]]; then
  echo "Live stable package differs from the validated signed bundle." >&2
  exit 1
fi
curl --fail --silent --show-error --max-time 30 "${UPDATE_PUBLIC_URL%/}/releases/$version/publisher-certificate-sha256.txt" > "$tmpdir/live-publisher.txt"
live_publisher="$(tr -d '[:space:]' < "$tmpdir/live-publisher.txt" | tr '[:lower:]' '[:upper:]')"
if [[ "$live_publisher" != "$expected_publisher" ]]; then
  echo "Archived production publisher fingerprint differs from the independent fingerprint." >&2
  exit 1
fi

backup_root="$(realpath "$(resolve_env_path "$BACKUP_ROOT")")"
case "$offsite_backup" in
  "$backup_root"|"$backup_root"/*)
    echo "Off-site backup must not be inside the production BACKUP_ROOT." >&2
    exit 1
    ;;
esac
primary_backup_sha="$(sha256sum "$primary_backup" | awk '{print toupper($1)}')"
offsite_backup_sha="$(sha256sum "$offsite_backup" | awk '{print toupper($1)}')"
if [[ "$primary_backup_sha" != "$offsite_backup_sha" ]]; then
  echo "Independent backup copy SHA-256 does not match the production backup." >&2
  exit 1
fi
if [[ "$(stat -c '%s' "$primary_backup")" -le 0 ]]; then
  echo "Production backup is empty." >&2
  exit 1
fi

python3 - "$pilot_evidence" "$version" "${APP_PUBLIC_URL%/}" "${UPDATE_PUBLIC_URL%/}/stable/release.json" "$expected_publisher" <<'PY'
import json, sys
path, version, server, manifest, publisher = sys.argv[1:]
data = json.load(open(path, encoding='utf-8'))
required_true = [
    'desktopSignatureValid', 'serviceSignatureValid', 'updaterSignatureValid',
    'serviceRunning', 'serviceDelayedAutoStart', 'serviceRecoveryConfigured',
    'updaterTaskEnabled', 'updaterTaskRunsAsSystem', 'rollbackBackupPresent',
    'workflowSmokeTestPassed', 'monitoringDisclosureConfirmed',
    'updatePathTestPassed', 'rollbackTestPassed'
]
if data.get('schemaVersion') != 1 or data.get('kind') != 'TaskMonitoringProductionPilotEvidence':
    raise SystemExit('Pilot evidence schema/kind is invalid.')
if str(data.get('expectedVersion')) != version or str(data.get('installedVersion')) != version:
    raise SystemExit('Pilot evidence version does not match the signed production release.')
if str(data.get('channel')) != 'stable':
    raise SystemExit('Pilot evidence is not for the stable channel.')
if str(data.get('serverUrl')).rstrip('/') != server.rstrip('/'):
    raise SystemExit('Pilot evidence server URL does not match APP_PUBLIC_URL.')
if str(data.get('updateManifestUrl')) != manifest:
    raise SystemExit('Pilot evidence update manifest URL does not match UPDATE_PUBLIC_URL.')
if str(data.get('publisherCertificateSha256')).upper() != publisher:
    raise SystemExit('Pilot evidence publisher fingerprint does not match.')
failed = [key for key in required_true if data.get(key) is not True]
if failed:
    raise SystemExit('Pilot evidence has failed/unconfirmed gates: ' + ', '.join(failed))
PY

pilot_sha="$(sha256sum "$pilot_evidence" | awk '{print toupper($1)}')"
accepted_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
python3 - "$output_path" "$accepted_at" "$version" "${APP_PUBLIC_URL%/}" "${UPDATE_PUBLIC_URL%/}" "$expected_manifest_sha" "$expected_package_sha" "$expected_package_size" "$expected_publisher" "$signed_workflow_reference" "$primary_backup_sha" "$(basename "$primary_backup")" "$(basename "$offsite_backup")" "$offsite_reference" "$pilot_sha" "$business_approval_reference" "$security_approval_reference" <<'PY'
import json, sys
(
    output, accepted_at, version, app_url, update_url, manifest_sha, package_sha,
    package_size, publisher, workflow_ref, backup_sha, primary_name, offsite_name,
    offsite_ref, pilot_sha, business_ref, security_ref
) = sys.argv[1:]
evidence = {
    'schemaVersion': 1,
    'kind': 'TaskMonitoringRealProductionLaunchAcceptance',
    'acceptedAtUtc': accepted_at,
    'release': {
        'version': version,
        'channel': 'stable',
        'manifestSha256': manifest_sha,
        'packageSha256': package_sha,
        'packageSizeBytes': int(package_size),
        'publisherCertificateSha256': publisher,
        'signedWorkflowReference': workflow_ref,
    },
    'production': {
        'appUrl': app_url,
        'updateUrl': update_url,
        'dnsResolved': True,
        'trustedTlsVerified': True,
        'healthReadyVerified': True,
        'serverAcceptancePassed': True,
        'publishedBytesMatchSignedBundle': True,
    },
    'backup': {
        'sha256': backup_sha,
        'primaryArchiveFile': primary_name,
        'independentCopyFile': offsite_name,
        'independentStorageReference': offsite_ref,
        'independentCopyHashVerified': True,
    },
    'pilot': {
        'evidenceSha256': pilot_sha,
        'signedInstallVerified': True,
        'workflowSmokeTestPassed': True,
        'updatePathTestPassed': True,
        'rollbackTestPassed': True,
        'monitoringDisclosureConfirmed': True,
    },
    'approvals': {
        'businessApprovalReference': business_ref,
        'securityApprovalReference': security_ref,
    },
    'accepted': True,
}
with open(output, 'w', encoding='utf-8', newline='\n') as handle:
    json.dump(evidence, handle, indent=2, sort_keys=True)
    handle.write('\n')
PY
chmod 600 "$output_path"
sha256sum "$output_path" > "$output_path.sha256"
chmod 600 "$output_path.sha256"

echo "REAL PRODUCTION LAUNCH ACCEPTANCE PASSED for TaskMonitoring $version"
echo "Evidence: $output_path"
echo "Evidence SHA-256: $(awk '{print toupper($1)}' "$output_path.sha256")"
