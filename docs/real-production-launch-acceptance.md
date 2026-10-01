# Real Production Launch Acceptance

This runbook is the final Domain 25 gate. It is intentionally different from CI acceptance: it must run against the organization's real production DNS/TLS endpoints, real signed release, real backup storage and a real office pilot PC.

The repository can provide and test the acceptance tooling, but Domain 25 is complete only when `accept-real-production-launch.sh` succeeds with real operator-owned inputs and emits a `TaskMonitoringRealProductionLaunchAcceptance` evidence file.

## 1. Production prerequisites

Before final acceptance:

- deploy the production stack with `docs/production-environment.md`;
- configure real public DNS names for `APP_PUBLIC_URL` and `UPDATE_PUBLIC_URL`;
- use publicly trusted HTTPS certificates;
- configure the organization-owned Windows code-signing certificate in GitHub Actions secrets;
- execute `.github/workflows/windows-release.yml` for the selected stable version;
- independently record the expected publisher certificate SHA-256 fingerprint;
- download the signed workflow artifact to a trusted administration workstation;
- publish the signed bundle using `scripts/production/publish-windows-release.sh`;
- create a fresh PostgreSQL backup and copy the exact backup archive to independently mounted/off-site storage;
- keep the signed workflow run/reference and business/security approval references available.

Never commit the signing PFX/private key, GitHub secret values, employee credentials, JWTs, refresh tokens or production database contents as acceptance evidence.

## 2. Prove the real Windows pilot

On a real pilot office PC, install the signed stable release using the normal production installer. Prove the update and rollback paths before collecting final evidence.

From an elevated PowerShell session in the trusted signed release bundle directory, run:

```powershell
./collect-production-pilot-evidence.ps1 `
  -ExpectedVersion "1.0.0" `
  -ServerUrl "https://task.example.com" `
  -UpdateManifestUrl "https://updates.example.com/stable/release.json" `
  -PublisherCertificateSha256 "<independently-verified-64-hex-fingerprint>" `
  -WorkflowSmokeTestPassed $true `
  -MonitoringDisclosureConfirmed $true `
  -UpdatePathTestPassed $true `
  -RollbackTestPassed $true `
  -OutputPath "C:\secure-evidence\production-pilot-evidence.json"
```

The collector refuses to write passing evidence unless it verifies:

- the installed stable version;
- Desktop, Service and Updater Authenticode signatures against the independently verified publisher fingerprint;
- executable versions;
- signed-package enforcement in updater settings;
- exact production server/update URLs;
- running delayed-auto Windows Service;
- configured service restart recovery;
- enabled SYSTEM updater task;
- an available local rollback backup;
- live production readiness and stable manifest reachability;
- explicit operator attestations that normal employee workflow smoke testing, monitoring disclosure, update-path testing and rollback testing passed.

The generated pilot evidence contains operational validation metadata only. It does not contain employee passwords, tokens, cookies, survey answers, screen content or signing private keys.

## 3. Prove independent backup storage

Create a fresh production backup:

```bash
primary_backup="$(bash scripts/production/backup-postgres.sh deploy/production/production.env launch)"
```

Copy that exact file to independently mounted/off-site storage according to organization policy. The final gate hashes both files and rejects the evidence when the bytes differ. It also rejects an "off-site" path located under the configured production `BACKUP_ROOT`.

A separate policy/ticket/reference must identify the independent storage location/retention decision. Do not put credentials in that reference.

## 4. Run the final real-environment gate

Move the pilot evidence JSON to the trusted production administration host without editing it. Then run:

```bash
bash scripts/production/accept-real-production-launch.sh \
  --env deploy/production/production.env \
  --release-bundle /secure/releases/TaskMonitoring.EmployeeRelease-1.0.0-win-x64 \
  --expected-publisher-sha256 '<independently-verified-64-hex-fingerprint>' \
  --primary-backup "$primary_backup" \
  --offsite-backup /mnt/independent-backup/taskmonitoring-launch.dump \
  --offsite-storage-reference 'backup-policy-or-ticket-reference' \
  --pilot-evidence /secure/evidence/production-pilot-evidence.json \
  --signed-workflow-reference 'github-actions-run-or-internal-reference' \
  --business-approval-reference 'business-go-live-approval-reference' \
  --security-approval-reference 'security-go-live-approval-reference' \
  --output /secure/evidence/taskmonitoring-production-launch-acceptance.json
```

The final gate fails closed unless all of the following are true:

1. production app and update URLs are non-localhost HTTPS URLs with resolvable DNS;
2. normal TLS/hostname validation succeeds (no insecure curl flag is used);
3. the production readiness endpoint succeeds;
4. the existing production server acceptance suite succeeds;
5. the signed bundle is a stable semantic-version release with valid package metadata;
6. bundle package bytes match its manifest;
7. bundle publisher fingerprint matches the independently supplied fingerprint;
8. the exact live stable manifest bytes match the validated signed bundle;
9. the exact live runtime package size/SHA-256 matches the signed bundle;
10. the archived production publisher fingerprint matches the independent fingerprint;
11. the primary and independently stored backup copies have the same SHA-256;
12. the independent backup is outside production `BACKUP_ROOT`;
13. the pilot evidence matches the production version, URLs and publisher fingerprint;
14. every required pilot validation/attestation is true;
15. signed-workflow, backup-storage, business-approval and security-approval references are supplied.

On success the script writes:

- `taskmonitoring-production-launch-acceptance.json`
- `taskmonitoring-production-launch-acceptance.json.sha256`

Keep these files in the organization's controlled release/audit evidence storage. They contain references and hashes, not secrets.

## 5. Completion rule

Repository CI proving that these scripts parse/build is not Domain 25 completion. Domain 25 becomes complete only after the final script succeeds against the real environment and the generated evidence is reviewed/retained under the organization's release process.

After that real evidence exists, `docs/project-completion-audit.md` may be updated from 24/25 (96%) to 25/25 (100%) with the non-secret evidence reference and acceptance timestamp.
