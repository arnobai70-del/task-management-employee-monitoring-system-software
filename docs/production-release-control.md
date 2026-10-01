# Production Release Control Center

The Production Release Control Center adds a centralized approval and verification layer around the existing signed Windows release and production publishing workflow.

It deliberately does **not** execute arbitrary shell commands, write to the mounted update host, hold code-signing private keys, or replace the existing operator-owned atomic publication script. The API keeps `/srv/updates` read-only and verifies the resulting state after an approved operator publication.

## Permissions

- `reports.read` — view release-control state and readiness in the Operations console.
- `production-releases.manage` — register candidates and perform approval, promotion, deployment verification and rollback decisions.

The new management permission is seeded through the normal permission catalog. Default SuperAdmin receives catalog permissions through the existing foundation seeding behavior; other roles must be explicitly granted it.

## Candidate registration

A candidate records immutable release metadata:

- numeric semantic version;
- full 40-character Git commit SHA;
- `release.json` SHA-256;
- runtime package file name, size and SHA-256;
- publisher certificate SHA-256 fingerprint;
- minimum updater version;
- signed workflow/artifact reference;
- signed-artifact verification attestation;
- independent publisher-fingerprint verification attestation.

A version number cannot be reused, even after withdrawal. This matches the production rule that different bytes must never share one version.

## Approval and promotion gates

A candidate must first be explicitly approved. Approval is rejected unless both signed-artifact and independent publisher-fingerprint verification are attested.

Production promotion authorization is blocked unless all automatic gates pass:

1. signed artifact verified;
2. publisher fingerprint independently verified;
3. API and PostgreSQL are healthy;
4. the latest production backup is known and fresh;
5. no critical operations incidents are open;
6. no critical security alerts are active;
7. no employee-agent rollout is active or paused;
8. the candidate version advances the observed stable version.

Older versions use the explicit rollback workflow instead of normal promotion.

## Operator publication boundary

After promotion is authorized, the operator publishes the already verified signed bundle with the existing atomic script:

```bash
bash scripts/production/publish-windows-release.sh \
  --env deploy/production/production.env \
  --bundle /secure/path/TaskMonitoring.EmployeeRelease-X.Y.Z-win-x64
```

The control center then verifies deployment. It does not trust only the reported version: it checks the mounted stable `release.json` hash and metadata, runtime package file/size/SHA-256, and the archived publisher fingerprint/certificate presence against the immutable candidate record.

Only after those checks pass is the release marked `Deployed`. A previously deployed release is retained as `Superseded` for rollback history.

## Rollback

Rollback can be requested only from the currently deployed release and only when a previously verified superseded release exists. Before accepting the rollback request, the service verifies that the archived target release still contains the expected manifest, package bytes/hash/size and publisher fingerprint/certificate.

The operator republishes that archived signed release through the same atomic publication script. `Verify rollback` then checks the live stable files against the rollback target before the current release becomes `RolledBack` and the target returns to `Deployed` state.

This design prevents the web/API layer from becoming a generic remote-execution surface while still centralizing the decision, gates, evidence and durable release history.

## Persistence and auditability

Release lifecycle state is stored as immutable `AuditLog` revisions under target type `ProductionRelease`. No database migration is required. Events include registration, approval, promotion authorization, deployment verification, supersede, rollback request, rollback verification/restoration and withdrawal.

The approval-time readiness snapshot is retained with the audit event so later review can see which gates were clear when promotion was authorized.

## Admin UI

The Operations page contains the Production Release Control Center with:

- current observed stable release;
- candidate/deployed/rollback counts;
- bound/enrolled managed-device counts;
- per-gate readiness matrix;
- signed release candidate registration form;
- permission-aware approve/withdraw/authorize/verify/rollback controls;
- immutable hashes, commit references and deployment timeline.

The existing centralized Agent Update section remains responsible for pilot/general employee-device rollout after a stable release is verified.

## Privacy and security boundary

This feature adds no employee-surveillance collection. It stores release metadata, administrator actions, operational readiness results and artifact integrity values only. It never stores signing private keys, employee passwords, access/refresh tokens, cookies, screenshots, keystrokes, browser history, survey answers or page content.
