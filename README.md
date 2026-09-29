# Task Management & Employee Monitoring System

A centralized, internet-required task management, attendance, survey operations, reporting, and transparent employee work-monitoring platform for development teams and survey/field teams.

## Status

Development is active. The repository now contains the secure backend foundation plus Employee Core, Attendance / Shift / Work Session Core, Project / Task Core, Survey / Field Operations Core, and Reporting / Dashboard API Core.

Implemented backend capabilities include:

- ASP.NET Core / .NET 10 API foundation.
- PostgreSQL + Entity Framework Core with source-controlled migrations.
- JWT access tokens and rotating opaque refresh tokens; only refresh-token hashes are stored.
- Database-backed roles and permissions, login lockout, authentication rate limiting, and security audit logging.
- Department administration and employee provisioning/profile management with department, supervisor, role, employment-status, and non-destructive deactivation support.
- Reporting-line cycle prevention, self-access protection, and active refresh-token revocation when an employee is deactivated.
- Timezone-aware shifts, effective-dated shift assignments, employee attendance self-service, late/early calculation, break tracking, overnight-shift handling, and work-session safeguards.
- Project lifecycle management, active/inactive project membership, project-bound tasks, guarded task transitions, assignment rules, immutable comments, and append-only task activity history.
- Project-scoped survey forms, typed questionnaires, publish locking, employee field assignment, draft/final submission, required/type/choice validation, immutable revision history, and supervisor approve/reject review.
- Read-only dashboard/reporting APIs for workforce, attendance, projects, tasks, employee workload, and survey progress.
- Dedicated permission boundaries for employee, attendance, project/task, survey, reporting, role, and audit access.
- Health/OpenAPI endpoints, warning-free Release builds, EF model-drift checks, automated business-rule tests, and real PostgreSQL migration/query validation in CI.

The employee desktop client, background Windows service, React admin web dashboard, notification system, realtime presence, installer, and update modules are not yet implemented.

## Architecture

```text
Employee PC
  -> Desktop Client (planned)
  -> Background Monitoring Service (planned)
  -> Internet / HTTPS
  -> ASP.NET Core API
  -> PostgreSQL
  -> Admin / Manager Dashboard (planned UI; reporting APIs implemented)
```

Normal employee functionality is intentionally internet/server dependent. The system is not designed as an offline-first application.

## Technology baseline

- Backend: ASP.NET Core / .NET 10
- Database: PostgreSQL 17
- ORM: Entity Framework Core
- Authentication: JWT access tokens + rotating opaque refresh tokens
- Authorization: database-backed roles and permissions
- API documentation: OpenAPI
- Tests: xUnit v3 on Microsoft Testing Platform
- CI: GitHub Actions
- Planned desktop: C#/.NET Windows application
- Planned background agent: .NET Windows Service
- Planned admin dashboard: React + TypeScript
- Planned realtime: SignalR

## Repository structure

```text
src/
  Backend/
    TaskMonitoring.Api/
      Contracts/
      Controllers/
      Data/
      Domain/
      Infrastructure/
      Migrations/
      Security/
      Services/
tests/
  Backend.Tests/
docs/
.github/workflows/
```

Additional clients will be added as their phases begin. Backend business rules should not be duplicated in clients.

## Employee Core API

The employee administration surface includes:

- `/api/departments` — department listing, creation, and updates.
- `/api/employees` — employee listing/search, provisioning, detail lookup, updates, and deactivation.
- `/api/roles` — role lookup used by employee administration.

Employee and department operations use dedicated permissions. Employee create/update can carry role assignments, so privilege-bearing role changes require `roles.manage` in addition to employee-management authority.

Employee records are not hard-deleted. Deactivation preserves audit/history references, disables the linked user account, and revokes active refresh tokens.

## Attendance / Shift / Work Session Core API

Shift administration uses `/api/shifts`:

- `GET /api/shifts` — list shifts, optionally filtered by active state (`shifts.read`).
- `POST /api/shifts` — create a shift (`shifts.manage`).
- `PUT /api/shifts/{id}` — update schedule, timezone, grace period, or active state (`shifts.manage`).
- `GET /api/shifts/assignments` — list shift assignments (`shifts.read`).
- `POST /api/shifts/assignments` — assign a shift for an effective date range (`shifts.manage`).

Attendance uses `/api/attendance`:

- `GET /api/attendance` — organization-wide work-session history (`attendance.read`).
- `GET /api/attendance/me/status` — authenticated employee's current attendance state.
- `POST /api/attendance/me/check-in` — start a work session.
- `POST /api/attendance/me/breaks/start` — start a break.
- `POST /api/attendance/me/breaks/end` — end the active break.
- `POST /api/attendance/me/check-out` — end the work session.

All persisted timestamps are UTC. Shift schedule interpretation uses the configured IANA timezone ID, so scheduled hours remain stable regardless of API-host or employee-device timezone. Overnight shifts map after-midnight activity to the shift's starting work date.

Only one work session may remain open for an employee through the normal service flow. Checkout is rejected while a break remains open, preserving explicit and auditable break durations.

## Project / Task Core API

Project administration uses `/api/projects`:

- `GET /api/projects` — search/filter/page projects (`projects.read`).
- `GET /api/projects/{id}` — project summary with active-member and open-task counts (`projects.read`).
- `POST /api/projects` — create a project (`projects.manage`).
- `PUT /api/projects/{id}` — update metadata, dates, or lifecycle state (`projects.manage`).
- `GET /api/projects/{projectId}/members` — list project membership (`projects.read`).
- `PUT /api/projects/{projectId}/members` — add/reactivate/update a member (`projects.manage`).
- `DELETE /api/projects/{projectId}/members/{employeeId}` — deactivate membership after open assignments are cleared (`projects.manage`).

Task operations use `/api/tasks`:

- `GET /api/tasks` — search/filter/page tasks by project, status, priority, or assignee (`tasks.read`).
- `GET /api/tasks/{id}` — task detail (`tasks.read`).
- `POST /api/tasks` — create a task (`tasks.manage`).
- `PUT /api/tasks/{id}` — edit task metadata/assignment (`tasks.manage`).
- `PUT /api/tasks/{id}/status` — validated workflow transition (`tasks.manage`).
- `GET /api/tasks/{taskId}/comments` — list comments (`tasks.read`).
- `POST /api/tasks/{taskId}/comments` — append a comment (`tasks.read` + `tasks.comment`).
- `GET /api/tasks/{taskId}/activities` — append-only activity history (`tasks.read`).

Tasks may only be assigned to active employees who are active project members. Removing a project member is blocked while that employee owns an open task. Completing or archiving a project is blocked until every task is `Done` or `Cancelled`; archived projects are immutable through the Project / Task Core service paths.

Task transitions are explicit: `ToDo -> InProgress/Blocked/Cancelled`, `InProgress -> Blocked/Done/Cancelled`, `Blocked -> InProgress/Cancelled`, `Done -> InProgress`, and `Cancelled -> ToDo`.

## Survey / Field Operations Core API

Survey form administration uses `/api/surveys`:

- `GET /api/surveys` — search/filter/page forms by project/status (`surveys.read`).
- `GET /api/surveys/{id}` — retrieve form metadata and ordered questionnaire (`surveys.read`).
- `POST /api/surveys` — create a draft project-scoped survey (`surveys.manage`).
- `PUT /api/surveys/{id}` — edit draft metadata (`surveys.manage`).
- `PUT /api/surveys/{id}/questions` — replace a draft questionnaire (`surveys.manage`).
- `PUT /api/surveys/{id}/status` — validated publish/close/archive lifecycle transition (`surveys.manage`).

Field assignment administration uses `/api/survey-assignments`:

- `GET /api/survey-assignments` — filter/page assignments (`survey.assignments.read`).
- `POST /api/survey-assignments` — assign a published survey to an active employee (`survey.assignments.manage`).
- `DELETE /api/survey-assignments/{id}` — cancel an eligible assignment (`survey.assignments.manage`).

Employee field work uses `/api/survey-assignments/me` and requires `survey.submit`:

- `GET /api/survey-assignments/me` — authenticated employee's active assignments.
- `POST /api/survey-assignments/me/{assignmentId}/draft` — save a draft response.
- `POST /api/survey-assignments/me/{assignmentId}/submit` — validate and submit a response.

Supervisor review uses `/api/survey-submissions` and requires `survey.review`:

- `GET /api/survey-submissions/pending` — page responses waiting for review.
- `POST /api/survey-submissions/{submissionId}/review` — approve/reject a submitted revision; rejection requires a comment.

Questionnaires become immutable after publication. Submitted revisions cannot be edited; a rejected response is retained and the next submission is stored as a new revision. Approved submissions remain immutable. Closing/archiving is blocked while a response is waiting for review.

## Reporting / Dashboard API Core

Reporting is read-only and derived from current operational tables; it does not maintain a separate reporting cache or reporting tables yet. All endpoints require `reports.read`.

- `GET /api/reports/dashboard?from=&to=` — dashboard snapshot covering active workforce, attendance, project status, task workload/overdue/completion, and survey review/progress metrics.
- `GET /api/reports/attendance/daily?from=&to=&departmentId=` — daily attendance trend with zero-filled dates and optional department filter.
- `GET /api/reports/projects?projectId=` — project progress with member/task totals, blocked/overdue counts, and completion percentage.
- `GET /api/reports/workload?departmentId=&limit=20` — active employee workload combining open tasks and active survey assignments.
- `GET /api/reports/surveys?projectId=` — survey assignment-state, overdue, and approval metrics by form.

Date-based reporting defaults to the latest 30-day window and rejects ranges longer than 366 days. Project completion percentage excludes cancelled tasks from the denominator. Survey approval percentage excludes cancelled assignments. Workload reports count survey assignments that are not approved or cancelled as active workload.

The attendance department filter reflects the employee's **current** department because historical department-at-attendance snapshots are not part of the current schema. If historical organization-chart reporting becomes a requirement, it should be added explicitly rather than inferred.

Reporting Core does not add EF entities or database tables, so no new migration is required for this phase. CI executes representative reporting queries through the real PostgreSQL provider in addition to in-memory business-rule coverage.

## Local prerequisites

- .NET 10 SDK
- Docker Desktop or a local PostgreSQL server
- Git

## Configuration

Copy `.env.example` to `.env` for Docker-oriented local configuration and replace every placeholder secret. Never commit `.env`.

Required API configuration:

- `ConnectionStrings__DefaultConnection`
- `Jwt__SigningKey` — at least 32 bytes of random secret material

Optional initial administrator configuration:

- `BootstrapAdmin__Email`
- `BootstrapAdmin__Password` — at least 12 characters

After bootstrap administrator creation, remove bootstrap credentials from the environment. Built-in roles and permissions are seeded idempotently after the database schema is available.

## Start PostgreSQL

```bash
cp .env.example .env
# Edit .env first and replace placeholder passwords/keys.
docker compose up -d postgres
```

## Database migrations

Restore the pinned EF tool, then apply committed migrations:

```bash
dotnet tool restore
dotnet ef database update \
  --project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj \
  --startup-project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj
```

Production should apply migrations deliberately during deployment and keep `Database__AutoMigrate=false`. Local development may set `Database__AutoMigrate=true` when automatic migration on API startup is useful.

After changing EF entities or mappings, add and commit a migration. CI rejects model changes without a matching migration and applies the full migration chain to a fresh PostgreSQL database.

## Run the API

Export required ASP.NET Core environment variables from your secret store or shell, ensure committed migrations have been applied, then:

```bash
dotnet restore TaskMonitoring.slnx
dotnet run --project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj
```

Health endpoint: `/health`

OpenAPI document: `/openapi/v1.json`

## Tests

```bash
dotnet tool restore
dotnet restore TaskMonitoring.slnx
dotnet build TaskMonitoring.slnx --configuration Release --no-restore
dotnet ef migrations has-pending-model-changes \
  --project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj \
  --startup-project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj \
  --configuration Release --no-build
dotnet test tests/Backend.Tests/Backend.Tests.csproj --configuration Release --no-build --no-restore
```

The PostgreSQL migration/integration test expects `TEST_POSTGRES_CONNECTION` to point to an isolated test database. GitHub Actions supplies one automatically.

CI validates a warning-free Release build, EF model/migration consistency, authentication behavior, Employee Core, Attendance Core, Project / Task Core, Survey / Field Operations Core, Reporting / Dashboard Core aggregates, and the complete migration/query path against PostgreSQL 17.

## Security and monitoring principles

- No plain-text passwords or committed production secrets.
- Server-side authentication, authorization, and validation are mandatory.
- Privilege-bearing role assignment requires explicit role-management authority.
- Refresh tokens are random opaque values; only their SHA-256 hashes are stored.
- Deactivating an employee disables the linked account and revokes active refresh tokens.
- Attendance self-service requires an authenticated, active employee profile.
- Organization-wide attendance, project/task, survey, reporting, role, and audit access use explicit permissions.
- `reports.read` is a separate organization-wide reporting permission; report endpoints do not implicitly inherit access from unrelated read permissions.
- Task assignment requires active project membership; project/member/task lifecycle changes are audited.
- Survey form management, assignment administration, field submission, and supervisor review use separate permission boundaries.
- Field workers can mutate only their own survey assignments; submitted/approved revisions cannot be silently overwritten.
- Hidden spyware behavior, keylogging, password capture, covert camera/microphone activation, and unrelated private-file collection are explicitly out of scope.
- Any future screenshot, app-usage, URL, or location telemetry must have a legitimate business need, clear disclosure, appropriate permissions, auditability, and retention controls.

## Development workflow

Large features use focused branches and meaningful commits. A feature is not complete until its backend/data/UI layers (where applicable), validation, permissions, error handling, tests, and documentation work together.

See [`docs/architecture.md`](docs/architecture.md) for the current architecture direction.
