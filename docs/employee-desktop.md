# Employee Desktop Client and Windows Agent

## Purpose

The Employee Desktop Client provides authenticated employee self-service for attendance, assigned project tasks, assigned RDP/IP/website access records, and a visible online-status heartbeat. The companion Windows Agent is intentionally credential-free and checks only whether the backend health endpoint is reachable.

The system requires network connectivity for operational actions. It does not provide an offline attendance queue because attendance timestamps and state transitions must be validated by the server.

## Privacy boundary

The desktop client is designed for transparent business monitoring. The current client does not collect or transmit:

- keystrokes or typed passwords;
- clipboard contents;
- microphone or camera recordings;
- hidden screenshots;
- browser history;
- unrelated private files; or
- application-window contents.

The authenticated heartbeat contains only the authenticated employee identity already known by the server, client version, platform name, last-seen time, and attendance state derived by the server. The Privacy & Status tab exposes this disclosure to the employee.

The Windows Agent does not receive employee credentials, access tokens, or refresh tokens. It calls only `/health` and logs connectivity state transitions locally through the standard .NET hosting/logging pipeline.

## Authentication and local secret handling

Employees sign in through the existing `/api/auth/login` endpoint. Access tokens remain in memory. The rotating refresh token is stored under the current Windows user profile and encrypted using Windows DPAPI with `DataProtectionScope.CurrentUser`.

A successful refresh replaces the stored refresh token. Signing out clears the local protected token even when the backend is unavailable.

Production employee clients require HTTPS. Plain HTTP is accepted only for loopback development endpoints such as `http://localhost`.

## Self-scoped backend endpoints

- `GET /api/desktop/me` returns only the authenticated employee's profile, attendance state, assigned tasks and assigned business access records.
- `POST /api/desktop/me/heartbeat` records minimal presence information for the authenticated employee.
- `GET /api/desktop/presence` is an administrative/team view protected by `employees.read`.

Employee identity for self-service is resolved from the JWT `sub` claim. The self-service endpoints do not accept an employee ID from the desktop client, preventing one employee from selecting another employee's scope.

Presence is considered online when the most recent heartbeat is within 150 seconds. The server recommends a 60-second heartbeat interval and the client constrains the accepted interval to 30-300 seconds.

## Attendance controls

The desktop client uses the existing server-validated self-attendance endpoints:

- check in;
- start break;
- end break; and
- check out.

The UI enables actions based on the current server state. The backend remains authoritative and rejects impossible transitions such as duplicate check-in or ending a break that is not open.

## Assigned work and access

The dashboard displays up to 100 currently returned assigned tasks and the employee's active RDP/website assignments plus non-released IP assignments. RDP records may include an approved external credential-manager reference, but reusable passwords, private keys and session cookies are not stored or displayed by this feature.

## Windows startup and service separation

The desktop UI can be started with Windows only when the employee enables the visible **Start desktop app with Windows** checkbox. This is stored in the current user's standard `Run` registry key.

The machine-level Windows Agent is a separate process. It does not launch the UI, does not run with an employee session token and does not impersonate the employee. This separation avoids exposing rotating user credentials to a service account and respects Windows Session 0 boundaries.

## Configuration

Both components read `TASK_MONITORING_SERVER_URL` when present. The desktop client otherwise reads `desktopsettings.json`; the Windows Agent otherwise reads `appsettings.json`.

Example production value:

```text
https://task-monitoring.example.com/
```

## Build and publish

From PowerShell:

```powershell
./scripts/publish-employee-client.ps1 -Runtime win-x64
```

This produces separate desktop and agent publish folders under `artifacts/employee-client` by default. The current milestone provides publish/install scripts rather than a signed MSI/MSIX installer.

## Install the Windows Agent

Run an elevated PowerShell session after publishing:

```powershell
./scripts/install-employee-agent.ps1 `
  -AgentDirectory ./artifacts/employee-client/agent-win-x64 `
  -ServerBaseUrl https://task-monitoring.example.com/
```

The installer creates `TaskMonitoringEmployeeAgent` with delayed automatic startup, configures restart-on-failure behavior and stores only the server URL as machine configuration.

To remove it:

```powershell
./scripts/uninstall-employee-agent.ps1
```

## Database

Presence is persisted in `employee_client_presence` with one current presence row per employee. The canonical EF migration for this milestone is `20260929201008_EmployeeClientPresence`.

## CI gates

Normal CI remains read-only. It validates:

- backend Release build with warnings as errors;
- EF model/migration consistency;
- backend tests against PostgreSQL 17 where applicable;
- Admin Web production build; and
- Windows Employee Desktop/Agent build plus client endpoint-security tests.

## Current limitations

This phase does not yet provide a signed installer, automatic application update channel, offline attendance submission, OS notification integration, or a dedicated Admin Web presence screen. Those can be added as separate milestones without weakening the self-scoping and privacy boundaries above.
