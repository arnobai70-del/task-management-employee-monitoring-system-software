# Architecture

## Direction

The product is an internet-required client-server system. The ASP.NET Core backend is authoritative for identity, authorization, attendance, projects/tasks, surveys, reporting, access assignments, presence and notification rules. The WPF Employee Desktop, visible Windows background service and React Admin Web are clients of that central backend.

The backend remains a modular monolith while the domain is evolving. This keeps deployment, authorization, transactions and schema management simple without duplicating business rules across clients.

## Repository layout

- `src/Backend/TaskMonitoring.Api` — ASP.NET Core REST API, SignalR hub, domain models, contracts, controllers and business services.
- `src/Backend/TaskMonitoring.Api/Migrations` — source-controlled Entity Framework Core migrations and model snapshot metadata.
- `src/AdminWeb` — React/TypeScript administration console and live workforce view.
- `src/EmployeeDesktop/TaskMonitoring.EmployeeDesktop` — .NET 10 WPF employee self-workspace.
- `src/EmployeeService/TaskMonitoring.EmployeeService` — visible Windows Service-compatible health/reachability worker.
- `scripts/windows` — explicit publish/install/uninstall tooling.
- `tests/Backend.Tests` — business-rule tests plus real PostgreSQL migration/query validation.
- `docs` — architecture and operational documentation.

## Backend foundation

The current foundation includes:

- PostgreSQL + Entity Framework Core.
- JWT access tokens with rotating opaque refresh tokens; only SHA-256 refresh-token hashes are persisted.
- Database-backed roles and permissions.
- Idempotent startup seeding for built-in roles/permissions after the schema exists.
- Permission claims and server-side authorization policies.
- Login lockout and API rate limiting.
- Security and operational audit records.
- Central exception handling with safe user-facing errors.
- Health and OpenAPI endpoints.
- ASP.NET Core SignalR for permission-scoped realtime delivery.
- CI checks for warning-free Release builds, EF model drift, business rules, PostgreSQL migration/query compatibility, Admin Web builds, and Windows desktop/service builds.

## Employee Core

Employee Core owns organization and employee identity:

- `Department` stores stable code/name, activation state and timestamps.
- `Employee` is a one-to-one profile attached to a `User` account.
- Employees may belong to a department and optionally report to another employee.
- Reporting-line cycles are rejected.
- Employee codes and user emails are normalized and unique.
- Provisioning creates the user, hashes the initial password, creates the employee profile and applies selected roles in one database unit of work.
- Deactivation disables both employee and user, revokes active refresh tokens and preserves historical references.
- Self-deactivation and self-role mutation are blocked through the employee administration path.

Employee create/update can carry role assignments, so those paths require both employee-management and role-management authority. Hard delete is intentionally absent because attendance, task, survey, access, audit and reporting records depend on stable employee references.

## Attendance / Shift / Work Session Core

### Shift model

- `Shift` stores code/name, local start/end times, IANA timezone ID, grace minutes, active state and timestamps.
- Equal start/end times are rejected; an earlier end time represents an overnight shift.
- `EmployeeShiftAssignment` binds an active employee to an active shift for an effective date range.
- Overlapping assignment ranges for the same employee are rejected.

All event timestamps are stored in UTC. Scheduled UTC boundaries are derived from the shift's local times and configured timezone. Overnight activity after midnight maps back to the shift's starting work date.

### Work-session model

- `WorkSession` captures employee, shift, assignment, work date, scheduled/actual timestamps, late minutes, early-leave minutes and total break minutes.
- `(EmployeeId, WorkDate)` is unique.
- A new check-in is blocked while any previous session is still open.
- `WorkBreak` records explicit break start/end and duration.
- A filtered unique index allows only one open break per work session.
- Checkout is rejected while a break remains open.

### Authorization

- `shifts.read` — shift/assignment lookup.
- `shifts.manage` — shift creation/update and employee assignment.
- `attendance.read` — organization-wide attendance history.
- Employee self-service attendance operations require authentication plus an active employee profile.

## Project / Task Core

### Project model

- `Project` stores unique code/name, description, lifecycle status, optional start/due dates and timestamps.
- Statuses: `Planning`, `Active`, `OnHold`, `Completed`, `Archived`.
- A project cannot complete/archive while open tasks remain.
- Archived projects are immutable through the Project / Task service paths.
- `ProjectMember` preserves membership history with `Member` or `Manager` role and active/inactive state.
- Removing a project member is blocked while that employee owns an open task.

### Task model

- `ProjectTask` stores project, title/description, priority, status, optional assignee/due date, creator, completion timestamp and audit timestamps.
- Assignees must be active employees and active project members.
- Task due dates must stay inside configured project date boundaries.
- Explicit transitions prevent arbitrary state jumps.
- `TaskComment` is append-only discussion.
- `TaskActivity` is append-only structured history for creation, edit, status change and comments.

### Authorization

- `projects.read` / `projects.manage`
- `tasks.read` / `tasks.manage`
- `tasks.comment`

## Survey / Field Operations Core

- `SurveyForm` belongs to a project and has `Draft`, `Published`, `Closed`, or `Archived` state.
- Forms/questions are editable only while draft; publishing requires at least one question and locks questionnaire structure.
- `SurveyQuestion` supports validated text, long text, number, boolean, date, single-choice and multiple-choice values.
- `SurveyAssignment` binds published forms to active employees.
- Employee self-service resolves the authenticated user to the employee and rejects cross-employee mutation.
- `SurveySubmission` uses immutable monotonically increasing revisions.
- Submitted/approved revisions are not overwritten; rejection requires a comment and later work creates another revision.

Permissions are separated into `surveys.read`, `surveys.manage`, `survey.assignments.read`, `survey.assignments.manage`, `survey.submit`, and `survey.review`.

## Reporting / Dashboard Core

Reporting is deliberately read-only. It queries existing operational tables and returns bounded projections for workforce, attendance, project progress, employee workload and survey progress. It does not create parallel reporting truth while the operational model is still evolving.

All Reporting Core endpoints require `reports.read`. This is intentionally separate from narrower source-screen permissions because organization-wide aggregates expose broader context. Representative reports execute through the real Npgsql/PostgreSQL provider in CI.

## Access Assignment Core

RDP, IP and website assignments are company-provided operational access records. Organization-wide workflows use dedicated `access.assignments.read` / `access.assignments.manage` permissions. Employee Desktop access is self-scoped through `/api/me/access`, where the server resolves the employee from the authenticated user.

Reusable RDP or website passwords are not stored or displayed by this module. Only approved username/credential references may be recorded for integration with an external credential-management process.

## Realtime Presence & Notifications

### Presence model

`EmployeePresence` stores one last-known presence row per employee:

- last heartbeat timestamp in UTC;
- disclosed client kind and optional client version;
- created/updated timestamps.

The WPF Employee Desktop sends an authenticated heartbeat every 30 seconds while the employee is signed in. Employee identity is derived from the JWT subject; the heartbeat request does not accept an employee ID.

Online/offline is computed server-side from heartbeat age. `Presence:OnlineThresholdSeconds` defaults to 90 seconds and is validated to 30–600 seconds. A missing/stale heartbeat becomes Offline even when a client crashes or loses connectivity without a clean disconnect.

Current work state is not trusted from the client. The server combines presence with attendance state:

- `Offline` — heartbeat is absent/stale;
- `OnBreak` — online with an open work session and open break;
- `Working` — online with an open work session and no open break;
- `Idle` — online without an open work session.

`GET /api/presence` requires `presence.read`. Admin Web loads an authoritative REST snapshot, receives SignalR `presenceChanged` deltas, and periodically refreshes the snapshot so heartbeat expiry is reflected without user action.

### Notification model

`EmployeeNotification` is durable per-employee state with kind, title/message, related entity metadata, creation timestamp and optional read timestamp. Current task notification kinds are assignment, unassignment/reassignment, task status change and task-detail update.

Task notifications are produced through an EF `SaveChangesInterceptor`. This makes the notification rule apply consistently when task mutations are saved, instead of relying on one controller/UI path. The durable notification is added to the same save operation as the task mutation. Realtime push occurs only after persistence succeeds; if realtime delivery fails, the durable inbox record remains available.

Self-service notification APIs resolve the employee from authentication and cannot read/mark another employee's notification by supplying an ID.

### SignalR authorization and groups

`/hubs/realtime` requires authentication.

- Accounts with `presence.read` join the `presence-readers` server group.
- Active employee accounts join a server-resolved `employee:{id}` group.
- Clients do not choose an employee group ID.
- Employee-specific task notifications are sent only to that employee group.

Both Admin Web and Employee Desktop obtain SignalR access tokens through their existing refresh-aware session logic. Access/refresh tokens are not added to Windows service configuration.

## Employee Desktop and Windows Service boundary

The Employee Desktop is the authenticated self-workspace. It owns employee login, attendance actions, task/access views, durable notification inbox, realtime task notifications and the disclosed presence heartbeat.

The Windows background service has a narrower trust boundary: it checks configured API reachability/service health and can run at Windows startup, but it does not persist employee JWT/refresh credentials. This prevents turning a machine-level service configuration into a reusable employee-session secret store.

## Database startup policy

Production should keep `Database:AutoMigrate=false` and apply committed migrations deliberately during deployment. After schema deployment, API startup idempotently seeds built-in identity metadata and optionally creates the one-time bootstrap administrator when bootstrap credentials are supplied.

Local development may set `Database:AutoMigrate=true` to apply committed migrations before seeding.

## Security boundaries

Passwords are hashed with ASP.NET Core `PasswordHasher<TUser>`. JWT signing keys, database passwords and bootstrap credentials are configuration secrets and must not be committed.

Attendance calculations, project/task lifecycle rules, survey rules, access scoping, presence calculation and notification ownership are server-controlled. Clients are presentation/interaction layers and must not redefine those rules.

Organization-wide reporting, access assignments and presence each have explicit permission boundaries. Employee `/api/me/*` endpoints resolve identity from the authenticated account rather than trusting an employee identifier from the client.

Presence is transparent and heartbeat-based; the Desktop privacy view discloses it. The feature does not imply keyboard, screen, camera, microphone or personal-file capture.

Hidden spyware behavior, keylogging, credential capture, covert screenshots, covert camera/microphone use and unrelated private-file collection remain out of scope. Any additional monitoring telemetry must be business-scoped, disclosed, permission-controlled, auditable and governed by explicit retention rules.

## Database changes

Schema changes are added as EF Core migrations and committed with the code that depends on them. CI runs `dotnet ef migrations has-pending-model-changes` and a real PostgreSQL migration-chain test, so a model change without matching migration metadata fails the build.

Realtime Presence & Notifications adds migration `20260929222350_RealtimePresenceNotifications`, after the existing access-assignment migration chain. It adds `employee_presences` and `employee_notifications` plus the required indexes/foreign keys. Reporting-only changes continue to require no schema migration unless their underlying operational model changes.
