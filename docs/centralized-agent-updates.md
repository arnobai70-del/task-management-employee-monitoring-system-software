# Centralized Employee Agent Update Management

The Admin Web **Operations** view includes centralized rollout control for the Employee Desktop, Windows Service and updater runtime. Rollout approval controls **when and which managed employee PCs may install the already-published stable release**; it does not replace the Windows updater's package verification.

## Production enrollment

Production deployment creates a dedicated secret file at `AGENT_UPDATE_ENROLLMENT_KEY_SECRET_FILE` (default `deploy/production/secrets/agent_update_enrollment_key`). The API receives it through Docker secrets as `AgentUpdates__EnrollmentKey`. Keep this secret in the organization secret manager and do not distribute it to employees.

To enroll a managed PC during elevated installation, pass the enrollment secret to `install-employee-windows.ps1` through `-AgentUpdateEnrollmentKey`. The installer uses it once to register the machine. The API returns a random per-device token; only a SHA-256 hash of that token is stored server-side in the immutable audit event stream.

The installer stores the per-device token in `%ProgramData%\TaskMonitoring\agent-update-device.json` with ACL access restricted to SYSTEM and local Administrators. Employee Desktop cannot read that credential. A separate non-secret `agent-update-device-id.json` is readable by Employee Desktop so authenticated health reporting can bind the enrolled machine to the signed-in employee account.

If `-AgentUpdateEnrollmentKey` is omitted, the existing direct updater schedule is retained for backward compatibility. Central rollout control therefore activates only on explicitly enrolled installations. If installation or upgrade rollback restores an already-enrolled installation, the hourly centralized runner schedule is restored as well rather than silently downgrading that PC to the legacy direct-updater schedule.

## Rollout lifecycle

Authorized users with `reports.read` can view rollout/device state. `agent-updates.manage` is required to create or control rollouts. SuperAdmin receives the permission through the normal permission seed.

A rollout targets only the **currently published stable release**. It can target selected active employees, one or more departments, or all active employees. Pilot rollouts are limited to 20 employees. An employee cannot belong to two Active/Paused rollouts at the same time.

Optional maintenance start/end timestamps are enforced by the device plan API. Rollouts can be paused, resumed or cancelled. A pilot can be promoted only after every pilot target reports the target version installed; the Admin Web promotion action expands a healthy pilot to all remaining active employees with an explicit confirmation.

Per-target states are:

- `WaitingForDevice` — target employee has no bound centrally enrolled device yet;
- `Pending` — device is bound and waiting for its scheduled approval check;
- `Deferred` — the updater deliberately deferred, for example because Employee Desktop is still running;
- `Downloading` — the elevated runner started the approved update attempt;
- `Installed` — target stable version is installed;
- `Failed` — updater returned a failure;
- `RolledBack` — a rollback state was observed during the failed update attempt.

When all targets report the target version installed, the rollout is marked Completed automatically. A fully installed Pilot remains eligible for explicit promotion after completion; adding the confirmed wider target set changes that same rollout to General/Active and continues the audited lifecycle.

## Windows execution boundary

The scheduled task runs as SYSTEM. In centralized mode it runs `run-central-agent-update.ps1` hourly. The runner first authenticates with the per-device token and asks the API whether that device is approved **right now**. If no rollout is assigned, the rollout is paused, or the maintenance window is closed, the updater is not executed.

When approval is present, the runner verifies that the public stable manifest version still matches the approved target and then invokes the existing `TaskMonitoring.EmployeeUpdater.exe`. The updater continues to enforce package size/SHA-256, executable version, and—when production signing is configured—the expected Authenticode publisher certificate. Central approval does not bypass any of those checks. The existing safe desktop-running defer remains in force unless an administrator explicitly uses a forced maintenance run.

CI release bundles remain unsigned development/acceptance artifacts. A production release is not considered signed unless real signing certificate material is supplied to the existing release pipeline.

## Incident Center integration

Device `Failed` or `RolledBack` rollout status is projected into the existing durable Operations Incident Center as a Critical `UpdateFailed` incident. The projection uses deterministic device+rollout identity, so repeated scans do not duplicate incidents. A later `Installed` status auto-resolves the incident. Manual incident resolution keeps the existing reopen cooldown behavior.

## Persistence and audit

This milestone does not add an EF table or migration. Device enrollment state, rollout lifecycle, target status and incident projection use the existing immutable `AuditLog` infrastructure. Device credentials are never returned by the Admin Web overview.

## Privacy boundary

Central update management records only operational metadata: device ID/machine name, employee binding, runtime/updater version, rollout/version/status/timestamps and administrative audit notes. It does **not** collect screenshots, screen content, keystrokes, passwords, cookies, external website pages/forms, balances, earnings, browsing history, or survey answers.
