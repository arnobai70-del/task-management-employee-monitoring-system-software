# Production Operations & Agent Health

The Admin Web **Operations** view gives authorized `reports.read` users a production-support view without extending employee-content monitoring.

## Server and recovery health

`GET /api/operations/overview` returns a current API/database readiness snapshot, including database probe latency and API process start/version. In production the API also reads the stable employee release manifest from the existing update publication root (`/srv/updates/stable/release.json` by default).

Successful PostgreSQL backups publish a small private status document into the API-only `operations_status` Docker volume. The document contains only the successful completion time, backup label, archive file name and size. The default backup warning threshold is 26 hours (`Operations:BackupStaleHours`). A backup can still complete successfully if this status publication is temporarily unavailable; the backup script emits a warning in that case rather than treating telemetry failure as backup failure.

## Employee agent health

The existing authenticated desktop presence heartbeat remains the durable source for online/offline and last-seen status. A signed-in Employee Desktop additionally sends a best-effort operational health report every 30 seconds containing:

- Windows machine name;
- Desktop, Windows Service and Updater executable versions when discoverable;
- whether `TaskMonitoringEmployeeService` is running;
- installed release version/channel from the local install state;
- last successful update time; and
- rollback time when the install state reports a rollback.

Detailed operational health is intentionally held in API process memory rather than added to the employee database. After an API restart it is shown as unavailable until the next signed-in desktop report. The default detailed-report stale threshold is 3 minutes (`Operations:DetailedAgentStaleMinutes`). Durable presence continues to show last-seen status independently.

The dashboard compares the reported installed/runtime version with the current stable release manifest and flags outdated installations. It also flags a stopped Windows Service, stale/missing detailed health, rollback state, and executable/install-state version mismatch.

## API

- `POST /api/me/agent-health` — authenticated employee; can only refresh the health snapshot linked to its own server-validated account.
- `GET /api/operations/overview?search=...&departmentId=...&health=...&limit=...` — `reports.read`.

The Admin Web polls the overview every 15 seconds and supports employee/department/machine search plus Healthy, Warning, Critical and Offline filters.

## Privacy boundary

This feature collects operational support metadata only. It does **not** collect screenshots, screen contents, keystrokes, passwords, cookies, external website form/page content, balances, earnings, browsing history, or survey answers. Machine/version/service/update metadata is used only to diagnose whether the installed TaskMonitoring runtime and server infrastructure are healthy.
