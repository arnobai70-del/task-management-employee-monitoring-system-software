# First Signed Production Release Checklist

This checklist defines the first real signed TaskMonitoring Windows rollout. CI can validate packaging and lifecycle behavior, but it cannot fabricate the organization's real DNS, TLS ownership or code-signing private key. Those production inputs must be configured by the operator before this checklist can be completed.

## Go/no-go prerequisites

Do not start the first signed release until all of these are true:

- the production server stack is deployed using `docs/production-environment.md`;
- `APP_PUBLIC_URL/health/live` and `/health/ready` succeed over trusted HTTPS;
- the Admin Web loads from the production application origin;
- the production update DNS name is live over trusted HTTPS;
- a current PostgreSQL backup has been copied to independent storage;
- the organization owns a valid Windows code-signing certificate with private key;
- repository secrets `WINDOWS_SIGNING_PFX_BASE64` and `WINDOWS_SIGNING_PFX_PASSWORD` are configured;
- the expected publisher certificate SHA-256 fingerprint has been recorded through an independent trusted channel;
- at least one pilot Windows PC is available for acceptance before broad rollout.

## 1. Choose the version and channel

Use numeric semantic versioning only:

```text
MAJOR.MINOR.PATCH
```

The first stable production release might be `1.0.0`. Do not reuse a version number for different bytes.

For the stable channel, the package base URL should match the production update origin and channel, for example:

```text
https://updates.example.com/stable
```

## 2. Run the signed GitHub Actions workflow

Run `.github/workflows/windows-release.yml` with:

- `version` — the selected version;
- `channel` — `stable` or `beta`;
- `package_base_url` — `${UPDATE_PUBLIC_URL}/stable` or `${UPDATE_PUBLIC_URL}/beta`.

The workflow must fail if signing secrets are absent. A successful workflow:

- builds self-contained Desktop, Service and Updater executables;
- Authenticode-signs the executables;
- signs installer, uninstaller, rollback and shared deployment PowerShell scripts;
- creates the versioned runtime ZIP;
- verifies package size/SHA-256 and executable file versions;
- validates signatures against the generated publisher fingerprint;
- uploads the signed release bundle as a workflow artifact;
- removes the temporary PFX from the runner.

A green normal CI run is not a substitute for a successful signed-release workflow.

## 3. Independently verify the release artifact

Download the signed workflow artifact to a trusted administration workstation.

Verify that the bundle contains:

```text
release.json
TaskMonitoring.EmployeeRuntime-X.Y.Z-win-x64.zip
updater/TaskMonitoring.EmployeeUpdater.exe
install-employee-windows.ps1
uninstall-employee-windows.ps1
rollback-employee-windows.ps1
deployment-common.ps1
publisher-certificate-sha256.txt
publisher-certificate.cer
```

Compare `publisher-certificate-sha256.txt` to the certificate fingerprint recorded independently before deployment. Do not establish trust solely from files downloaded from the same release/update host.

## 4. Publish to the update host atomically

On the production server, publish the validated bundle:

```bash
bash scripts/production/publish-windows-release.sh \
  --env deploy/production/production.env \
  --bundle /secure/path/TaskMonitoring.EmployeeRelease-X.Y.Z-win-x64
```

The runtime package is copied before the channel manifest is advanced.

Then run server acceptance:

```bash
bash scripts/production/acceptance-server.sh \
  --env deploy/production/production.env \
  --channel stable
```

Do not install pilot PCs until this passes.

## 5. Pilot first installation

From an elevated PowerShell session on a clean pilot PC:

```powershell
./install-employee-windows.ps1 `
  -ReleaseDirectory "." `
  -ServerUrl "https://task.example.com" `
  -UpdateManifestUrl "https://updates.example.com/stable/release.json" `
  -PublisherCertificateSha256 "<independently-verified-64-hex-fingerprint>"
```

Verify on the pilot PC:

- Add/Remove Programs contains TaskMonitoring Employee Workspace with the expected version;
- the Start Menu/Desktop shortcut opens the expected signed Desktop executable;
- the Desktop server URL is preloaded and locked;
- `TaskMonitoringEmployeeService` is Running and configured for delayed automatic start;
- service recovery actions are present;
- the automatic update scheduled task exists under SYSTEM;
- `%ProgramData%\TaskMonitoring\install-state.json` shows the expected version/channel;
- updater/service configuration contains no employee password, JWT or refresh token;
- sign-in works against the production API;
- attendance, tasks, notification and approved access views load normally;
- the monitoring disclosure is visible before relying on monitoring telemetry operationally.

## 6. Prove the update path before broad rollout

Build and publish the next controlled version (for example `1.0.1`) to a pilot/beta channel first, or advance stable only during the approved pilot window.

On the pilot PC, run the installed updater manually from an elevated shell if an immediate test is required:

```powershell
& "$env:ProgramData\TaskMonitoring\Updater\TaskMonitoring.EmployeeUpdater.exe" `
  --settings "$env:ProgramData\TaskMonitoring\update-settings.json" `
  --force
```

Confirm:

- install state advances to the new version;
- Desktop/Service file versions match;
- the service returns to Running;
- the prior version exists under `%ProgramData%\TaskMonitoring\backups`;
- normal employee workflows still work.

The scheduled updater normally defers instead of force-closing an active Desktop session. `--force` is for an approved pilot/maintenance test, not normal unattended behavior.

## 7. Prove rollback

Use the signed rollback command from the trusted release bundle:

```powershell
./rollback-employee-windows.ps1 `
  -PublisherCertificateSha256 "<independently-verified-64-hex-fingerprint>"
```

The rollback command restores the latest automatic-update backup, validates publisher signatures, restarts the service, retains a safety copy of the pre-rollback files and disables the automatic-update task by default.

After rollback:

- confirm the expected previous Desktop/Service version;
- confirm the service is Running;
- sign in and exercise a small employee workflow;
- confirm the scheduled updater is Disabled so the rejected release is not immediately reinstalled.

Correct or withdraw the bad channel manifest before re-enabling automatic updates.

## 8. Broad rollout gates

Broad rollout is approved only when:

- server acceptance passes;
- signed-release workflow is green;
- independent publisher fingerprint verification is complete;
- pilot installation passes;
- pilot automatic/manual update passes;
- rollback passes;
- PostgreSQL backup/restore procedure has a recent successful acceptance result;
- an operator knows where server, service and updater logs are located;
- the previous signed package remains available during the rollback window.

Roll out in small batches where operationally possible rather than every office PC at once.

## What CI proves automatically

Normal CI validates the mechanics without real production secrets:

- production Docker build/Compose model;
- key-per-file API secrets;
- explicit migrations and PostgreSQL readiness;
- real custom-format backup + data mutation + restore assertion;
- update-host atomic publication/download/SHA-256 behavior;
- Windows release-bundle construction;
- Windows install -> update -> rollback -> uninstall lifecycle using unsigned development-only packages on an isolated runner.

These tests deliberately use explicit development overrides for unsigned/local HTTP Windows acceptance. Production machines must not use those overrides.

## What remains operator-owned

The repository cannot itself supply or verify ownership of:

- real DNS records;
- public TLS reachability;
- real code-signing certificate/private key;
- organization backup retention/storage policy;
- pilot PC/network policy;
- final business approval to roll out employee monitoring functionality.

Therefore the first real signed production release must not be marked complete until the operator-owned gates above have been executed with the real environment.
