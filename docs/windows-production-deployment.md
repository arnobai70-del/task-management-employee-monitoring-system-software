# Windows Production Deployment and Auto Update

This document defines the production deployment contract for the TaskMonitoring Employee Desktop, Employee Service and Employee Updater.

## Goals

The Windows deployment path is designed to provide:

- a self-contained `win-x64` runtime package, so office PCs do not need a separately installed .NET runtime;
- versioned `MAJOR.MINOR.PATCH` releases;
- Authenticode-signed Employee Desktop, Employee Service and Employee Updater executables;
- SHA-256 verification of the runtime archive;
- a pinned publisher certificate fingerprint for auto-update authenticity;
- a machine-wide installer with rollback, service recovery, shortcuts and Add/Remove Programs registration;
- a scheduled SYSTEM updater that never force-closes an active employee Desktop session;
- rollback to the previous application files when service activation fails;
- separate API liveness and database-readiness probes.

The updater does not collect employee activity, credentials or personal data. Its only network purpose is release retrieval.

## Release bundle layout

`scripts/windows/build-employee-release.ps1` produces a directory similar to:

```text
TaskMonitoring.EmployeeRelease-1.4.0-win-x64/
  release.json
  TaskMonitoring.EmployeeRuntime-1.4.0-win-x64.zip
  updater/
    TaskMonitoring.EmployeeUpdater.exe
  install-employee-windows.ps1
  uninstall-employee-windows.ps1
  deployment-common.ps1
  publisher-certificate-sha256.txt   # signed production build only
  publisher-certificate.cer          # signed production build only
```

The runtime ZIP contains only versioned Desktop and Service runtime files. Environment-specific `desktop-settings.json` and service `appsettings.json` are deliberately excluded and are written by the installer on each machine.

`release.json` schema version 1 contains:

- channel (`stable` or `beta`);
- application version;
- publish timestamp;
- minimum compatible updater version;
- runtime ZIP file name / optional absolute package URL;
- runtime ZIP SHA-256;
- runtime ZIP size.

If `package.url` is omitted, the updater resolves `package.file` relative to the configured manifest URL.

## Code-signing setup

The production GitHub Actions workflow is `.github/workflows/windows-release.yml`. It intentionally fails when production signing secrets are missing.

Configure these repository secrets before producing a production release:

- `WINDOWS_SIGNING_PFX_BASE64` — base64-encoded PFX containing the organization code-signing certificate and private key;
- `WINDOWS_SIGNING_PFX_PASSWORD` — password for that PFX.

The workflow materializes the PFX only on the ephemeral Windows runner, signs the Desktop, Service, Updater and bootstrap PowerShell scripts, validates the signed bundle, removes the temporary PFX, then uploads the release bundle as a workflow artifact.

Private signing material must never be committed to this repository or copied into the release bundle.

The SHA-256 certificate fingerprint written into `publisher-certificate-sha256.txt` is an operational reference. For first installation, administrators should verify the expected fingerprint through a trusted channel independent of the download location. Do not establish first-install trust by blindly copying a fingerprint from an untrusted download source.

## Building a signed release locally

On a trusted Windows release workstation with the .NET 10 SDK and Windows SDK signing tools installed:

```powershell
./scripts/windows/build-employee-release.ps1 `
  -Version "1.4.0" `
  -Channel stable `
  -PackageBaseUrl "https://updates.example.com/taskmonitoring/stable" `
  -PfxPath "C:\secure\company-code-signing.pfx" `
  -PfxPassword $env:TASKMONITORING_PFX_PASSWORD
```

`-PackageBaseUrl` is optional. When omitted, host `release.json` and its runtime ZIP in the same HTTPS directory.

CI uses `-AllowUnsignedDevelopmentBuild` only to validate packaging mechanics. That switch is not a production release mode.

## Publishing an update channel

The installed updater needs a stable manifest URL that remains constant across releases, for example:

```text
https://updates.example.com/taskmonitoring/stable/release.json
```

For each release:

1. Build and validate the signed bundle.
2. Upload the new runtime ZIP to the update host.
3. Upload/replace the channel `release.json` only after the ZIP is fully available.
4. Keep the previous runtime available long enough for operational rollback/diagnostics.

Publish the package before the manifest. This prevents clients from observing a manifest that references a package that has not finished uploading.

A release-specific URL such as `/1.4.0/release.json` is useful for archives, but it should not be configured as the machine's update URL unless it is intentionally immutable. Automatic updates require a stable channel pointer that advances to newer releases.

## First installation

Run from an elevated PowerShell session. For production, use HTTPS for both the API and update manifest and provide the independently verified publisher certificate SHA-256 fingerprint:

```powershell
./install-employee-windows.ps1 `
  -ReleaseDirectory "." `
  -ServerUrl "https://task-api.example.com" `
  -UpdateManifestUrl "https://updates.example.com/taskmonitoring/stable/release.json" `
  -PublisherCertificateSha256 "<64-hex-character-certificate-sha256>"
```

The installer performs these operations:

1. verifies `release.json` structure and runtime ZIP SHA-256;
2. performs an API `/health/live` preflight unless explicitly skipped;
3. extracts to a staging directory under `%ProgramData%\TaskMonitoring`;
4. verifies Desktop, Service and Updater Authenticode signatures against the pinned publisher certificate;
5. refuses to replace files while Employee Desktop is running unless a maintenance operator explicitly selects `-ForceCloseDesktop`;
6. backs up an existing installation before replacement;
7. installs application files under `%ProgramFiles%\TaskMonitoring`;
8. writes machine-managed Desktop and Service configuration without credentials;
9. installs `TaskMonitoringEmployeeService` with delayed automatic startup and restart-on-failure recovery;
10. creates Start Menu and (by default) Public Desktop shortcuts;
11. installs the updater under `%ProgramData%\TaskMonitoring\Updater`;
12. creates a SYSTEM scheduled update task every four hours;
13. registers the product in Windows Add/Remove Programs;
14. starts the Employee Service;
15. restores the previous installation if activation fails.

Production installs do not persist employee JWTs, refresh tokens or passwords. Employee authentication tokens remain process-memory-only as before.

`-AllowHttpForDevelopment` and `-AllowUnsignedDevelopmentBuild` are explicit non-production escape hatches. They should not be used on office production machines.

## Desktop server configuration

The installer writes:

```text
%ProgramFiles%\TaskMonitoring\Desktop\desktop-settings.json
```

with the organization API URL and `lockServerUrl=true`. The Desktop preloads that URL and makes the server field read-only. A developer build without this file retains the manual server URL field.

## Service configuration and recovery

The installer writes only these service settings:

- API base URL;
- heartbeat interval;
- explicit development-only HTTP allowance;
- logging levels.

The service fails fast when the URL is invalid, when production configuration uses HTTP, or when the heartbeat interval is outside 15–3600 seconds.

The service requests:

```text
GET /health/live
```

and logs machine name, API host, reachability and status code. It does not use employee authentication credentials.

Windows Service Control Manager is configured for delayed automatic start and restart recovery after service failures.

## Automatic update behavior

The SYSTEM scheduled task runs `TaskMonitoring.EmployeeUpdater.exe` every four hours using `%ProgramData%\TaskMonitoring\update-settings.json`.

The updater:

- uses a global mutex to prevent overlapping runs;
- requires the configured update channel to match the manifest channel;
- rejects malformed/non-numeric versions;
- refuses releases that require a newer updater;
- downloads the runtime ZIP over the configured HTTP(S) policy;
- verifies the runtime ZIP SHA-256;
- verifies the signed Desktop and Service publisher against the pinned certificate in production mode;
- defers with exit code `10` when Employee Desktop is running instead of terminating the user's session;
- stops the Employee Service only after the package is fully downloaded and verified;
- moves the current Desktop/Service directories into a backup;
- preserves environment-specific Desktop/Service configuration;
- activates the new directories and restarts the service;
- rolls files back and attempts to restart the previous service when activation fails;
- retains a bounded number of previous backups;
- writes update logs to `%ProgramData%\TaskMonitoring\logs\updater.log` with simple size rotation.

Updater exit codes used operationally:

- `0` — no update required or update completed successfully;
- `10` — update available but deferred because Employee Desktop is running;
- `20` — update/configuration/verification/activation failure;
- `30` — release requires a newer updater; manual maintenance is required.

## Publisher certificate rotation

The updater pins the SHA-256 fingerprint of the current production publisher certificate. Therefore certificate rotation must be staged intentionally:

1. deploy a trusted installer/updater configuration that accepts the new publisher certificate before switching release signing;
2. verify the new pinned fingerprint on target machines;
3. begin signing new releases with the new certificate;
4. do not simply change the fingerprint inside the remotely hosted manifest—the updater does not trust a remote manifest to redefine its publisher trust anchor.

This design prevents a compromised update host from replacing the trusted publisher certificate.

## Uninstall

Windows Add/Remove Programs invokes the installed uninstaller. It can also be run directly from an elevated PowerShell session.

By default, application files, service, shortcuts, updater and scheduled task are removed while operational logs/backups under `%ProgramData%\TaskMonitoring` are retained for administrators.

For full cleanup:

```powershell
./uninstall-employee-windows.ps1 -RemoveProgramData
```

The uninstaller refuses to proceed while Employee Desktop is running.

## API health endpoints

The API now exposes:

- `/health/live` — process liveness only; does not depend on PostgreSQL;
- `/health/ready` — readiness including PostgreSQL connectivity;
- `/health` — compatibility alias for readiness.

Use `/health/live` for restart/liveness decisions and `/health/ready` for load-balancer traffic admission or deployment readiness.

Production should keep `Database:AutoMigrate=false` and apply committed migrations as an explicit deployment step before directing traffic to a new API version.

## Recommended deployment sequence

1. Back up PostgreSQL according to your database operations policy.
2. Apply committed EF Core migrations deliberately.
3. Deploy the API/Admin Web.
4. Confirm `/health/live` and `/health/ready` return success.
5. Build and publish a signed Windows release bundle.
6. Advance the stable update manifest only after its runtime ZIP is available.
7. Install new PCs using the signed bootstrap bundle and pinned publisher fingerprint.
8. Monitor service/updater logs during rollout.
9. Keep the previous package available during the rollback window.
