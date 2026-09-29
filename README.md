# Task Management & Employee Monitoring System

A centralized, internet-required task management, attendance, survey operations, and transparent employee work-monitoring platform for development teams and survey/field teams.

## Status

Development is active. The repository now contains the secure backend foundation, Employee Core, Attendance / Shift / Work Session Core, Project / Task Core, and Survey / Field Operations Core.

Implemented backend capabilities include:

- ASP.NET Core / .NET 10 API foundation.
- PostgreSQL + Entity Framework Core with source-controlled migrations.
- JWT access tokens and rotating opaque refresh tokens.
- Database-backed roles and permissions.
- Login lockout, authentication rate limiting, and security audit logging.
- Department create/read/update and activation state management.
- Employee provisioning with user account creation, password hashing, department assignment, supervisor assignment, and role assignment.
- Employee search/filter/paging and profile updates.
- Reporting-line cycle prevention and self-access protection.
- Non-destructive employee deactivation with active refresh-token revocation.
- Permission-protected Employee Core APIs; role assignment requires both employee-management and role-management authority.
- Timezone-aware shift definitions with configurable grace periods.
- Non-overlapping employee shift assignments with effective date ranges.
- Employee self-service attendance status, check-in, break start/end, and check-out.
- Late-arrival, early-leave, and accumulated break-minute calculation.
- Overnight-shift work-date handling and open-session safeguards.
- Organization-wide attendance/work-session listing with date, employee, and pagination filters.
- Dedicated shift/attendance permissions and audit events for shift and attendance lifecycle changes.
- Project creation/update, lifecycle status, date boundaries, search/filter/paging, and non-destructive archive behavior.
- Active/inactive project membership with member/manager roles and open-task removal safeguards.
- Project tasks with assignment, priority, due date, guarded status transitions, completion timestamps, search/filter/paging, and project-bound due-date validation.
- Task assignees restricted to active project members.
- Immutable task comments and append-only task activity history for creation, edits, status changes, and comments.
- Dedicated `projects.*` and `tasks.*` permissions plus project/task audit events.
- Project-scoped survey forms with `Draft`, `Published`, `Closed`, and `Archived` lifecycle states.
- Typed survey questions covering text, long text, number, boolean, date, single-choice, and multiple-choice inputs.
- Questionnaire locking after publication, unique question keys, choice-option validation, and required-answer enforcement on final submission.
- Field survey assignment restricted to active employees and published surveys.
- Employee-owned field workflow for viewing assignments, saving drafts, and submitting responses.
- Server-side answer type and configured-choice validation.
- Rejection/resubmission as immutable numbered revisions so prior responses remain preserved.
- Separate supervisor review flow with approve/reject decisions and mandatory rejection comments.
- Dedicated survey/form, assignment, submit, and review permissions plus survey audit events.
- Health/OpenAPI endpoints, automated tests, EF model-drift checks, and PostgreSQL migration validation in CI.

CI validates a warning-free Release build, Entity Framework model/migration consistency, authentication behavior, Employee Core, Attendance Core, Project / Task Core, and Survey / Field Operations Core business rules, and the complete migration chain against a fresh PostgreSQL service.

The employee desktop client, background Windows service, admin web dashboard, reporting, notification, realtime-presence, installer, and update modules are not yet implemented.

## Architecture

```text
Employee PC
  -> Desktop Client
  -> Background Monitoring Service
  -> Internet / HTTPS
  -> ASP.NET Core API
  -> PostgreSQL
  -> Admin / Manager Dashboard
```

Normal employee functionality is intentionally internet/server dependent. The system is not designed as an offline-first application.

## Technology baseline

- Backend: ASP.NET Core / .NET 10
- Database: PostgreSQL
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
      Controllers/
      Domain/
      Services/
      Migrations/
tests/
  Backend.Tests/
docs/
.github/workflows/
```

Additional clients will be added as their phases begin; backend business rules should not be duplicated in clients.

## Employee Core API surface

The current employee administration surface includes:

- `/api/departments` — department listing, creation, and updates.
- `/api/employees` — employee listing/search, provisioning, detail lookup, and updates/deactivation.
- `/api/roles` — role lookup used by employee administration.

Employee and department read/write operations use dedicated permission policies. Creating or updating an employee includes role assignment, so those write operations also require `roles.manage`; this prevents an employee manager from escalating another account to a privileged role without role-management authority.

Employee records are not hard-deleted. Deactivation preserves audit/history references, disables the linked user account, and revokes its active refresh tokens.

## Attendance Core API surface

Shift administration uses `/api/shifts`:

- `GET /api/shifts` — list shifts, optionally filtered by active state (`shifts.read`).
- `POST /api/shifts` — create a shift (`shifts.manage`).
- `PUT /api/shifts/{id}` — update schedule, timezone, grace period, or activation state (`shifts.manage`).
- `GET /api/shifts/assignments` — list shift assignments (`shifts.read`).
- `POST /api/shifts/assignments` — assign a shift to an employee for an effective date range (`shifts.manage`).

Attendance uses `/api/attendance`:

- `GET /api/attendance` — organization-wide work-session history (`attendance.read`).
- `GET /api/attendance/me/status` — authenticated employee's current attendance state.
- `POST /api/attendance/me/check-in` — start the employee's work session.
- `POST /api/attendance/me/breaks/start` — start a break.
- `POST /api/attendance/me/breaks/end` — end the active break.
- `POST /api/attendance/me/check-out` — end the work session.

All persisted timestamps are UTC. Shift schedule interpretation uses the shift's configured IANA timezone ID, so local working hours remain stable when the API host or employee device uses a different timezone. Overnight shifts map after-midnight activity to the shift's starting work date.

Only one work session may remain open for an employee through the normal service flow. A new check-in is blocked until an older open session is checked out. Checkout is also blocked while a break remains open, preserving explicit and auditable break durations.

## Project / Task Core API surface

Project administration uses `/api/projects`:

- `GET /api/projects` — search/filter/page projects (`projects.read`).
- `GET /api/projects/{id}` — retrieve a project summary with active-member and open-task counts (`projects.read`).
- `POST /api/projects` — create a project (`projects.manage`).
- `PUT /api/projects/{id}` — update metadata, dates, or lifecycle status (`projects.manage`).
- `GET /api/projects/{projectId}/members` — list project membership (`projects.read`).
- `PUT /api/projects/{projectId}/members` — add, reactivate, or update a member role (`projects.manage`).
- `DELETE /api/projects/{projectId}/members/{employeeId}` — deactivate membership after open assignments are cleared (`projects.manage`).

Task operations use `/api/tasks`:

- `GET /api/tasks` — search/filter/page tasks by project, status, priority, or assignee (`tasks.read`).
- `GET /api/tasks/{id}` — retrieve task details (`tasks.read`).
- `POST /api/tasks` — create a task (`tasks.manage`).
- `PUT /api/tasks/{id}` — edit title, description, priority, due date, or assignee (`tasks.manage`).
- `PUT /api/tasks/{id}/status` — perform a validated workflow transition (`tasks.manage`).
- `GET /api/tasks/{taskId}/comments` — list task comments (`tasks.read`).
- `POST /api/tasks/{taskId}/comments` — append a comment (`tasks.read` + `tasks.comment`).
- `GET /api/tasks/{taskId}/activities` — list append-only task activity history (`tasks.read`).

Tasks may only be assigned to active employees who are active members of the project. Removing a project member is blocked while that employee owns an open task. Completing or archiving a project is blocked until every task is `Done` or `Cancelled`, and archived projects are immutable through the Project / Task Core service paths.

Task status transitions are explicit rather than arbitrary: `ToDo` may move to `InProgress`, `Blocked`, or `Cancelled`; `InProgress` may move to `Blocked`, `Done`, or `Cancelled`; `Blocked` may return to `InProgress` or be cancelled; completed tasks may be reopened to `InProgress`; cancelled tasks may be restored to `ToDo`.

Project/task records are not hard-deleted. Membership deactivation, task comments, task activity records, and global audit events preserve operational history for reporting and accountability.

## Survey / Field Operations Core API surface

Survey form administration uses `/api/surveys`:

- `GET /api/surveys` — search/filter/page survey forms by project or status (`surveys.read`).
- `GET /api/surveys/{id}` — retrieve form metadata and ordered questionnaire (`surveys.read`).
- `POST /api/surveys` — create a draft project-scoped survey (`surveys.manage`).
- `PUT /api/surveys/{id}` — edit draft survey metadata (`surveys.manage`).
- `PUT /api/surveys/{id}/questions` — replace a draft questionnaire (`surveys.manage`).
- `PUT /api/surveys/{id}/status` — publish, close, or archive using validated lifecycle transitions (`surveys.manage`).

Field assignment administration uses `/api/survey-assignments`:

- `GET /api/survey-assignments` — filter/page field assignments by survey, employee, or status (`survey.assignments.read`).
- `POST /api/survey-assignments` — assign a published survey to an active employee (`survey.assignments.manage`).
- `DELETE /api/survey-assignments/{id}` — cancel an assignment when its current state allows cancellation (`survey.assignments.manage`).

Employee field work uses `/api/survey-assignments/me` and requires `survey.submit`:

- `GET /api/survey-assignments/me` — list the authenticated employee's active field assignments.
- `POST /api/survey-assignments/me/{assignmentId}/draft` — save a draft response.
- `POST /api/survey-assignments/me/{assignmentId}/submit` — validate required/type/choice rules and submit the response.

Supervisor review uses `/api/survey-submissions` and requires `survey.review`:

- `GET /api/survey-submissions/pending` — page responses waiting for review.
- `POST /api/survey-submissions/{submissionId}/review` — approve or reject a submitted revision; rejection requires a review comment.

Survey questions become immutable after publication so submitted data always remains interpretable against the questionnaire used to collect it. Employees can mutate only assignments linked to their own active employee profile. A submitted revision cannot be edited; when a reviewer rejects it, the next employee submission is stored as a new revision number while the rejected revision is retained. Approved submissions remain immutable.

Closing or archiving a survey is blocked while a response is waiting for review. Survey lifecycle, assignment, draft/submit, and review changes create audit records. The permission catalog defines separate read/manage/assignment/submit/review capabilities; administrators must explicitly assign appropriate permissions to operational roles according to organizational policy.

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

After the bootstrap administrator is created, remove those bootstrap credentials from the environment. Built-in roles and permissions are seeded idempotently at API startup after the database schema is available.

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

After changing EF entities or mappings, add and commit a migration. CI rejects model changes that do not have a matching migration and also applies the complete migration chain to a fresh PostgreSQL database.

## Run the API

Export the required ASP.NET Core environment variables from your secret store or shell, ensure the database migrations have been applied, then:

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

The database migration test expects `TEST_POSTGRES_CONNECTION` to point to an isolated test PostgreSQL database. GitHub Actions supplies one automatically.

## Security principles

- No plain-text passwords.
- No secrets or production credentials in source control.
- Server-side authentication, authorization, and validation are mandatory.
- Privilege-bearing role assignment requires explicit role-management authority.
- Refresh tokens are random opaque values and only their SHA-256 hashes are stored.
- Deactivating an employee revokes active refresh tokens and disables the linked account.
- Attendance self-service operations require an authenticated, active employee profile.
- Administrative shift and organization-wide attendance access use dedicated permissions.
- Project/task read, management, and task-comment operations use dedicated permissions.
- Task assignment requires active project membership and project/member/task lifecycle changes are audit logged.
- Survey form management, assignment administration, field submission, and supervisor review use separate permission boundaries.
- Field workers can save or submit only their own assignments, and approved/submitted survey revisions cannot be silently overwritten.
- Important authentication, administration, attendance, project, task, and survey events are audited.
- Hidden spyware behavior, keylogging, password capture, covert camera/microphone activation, and unrelated private-file collection are explicitly out of scope.
- Any future screenshot, app-usage, URL, or location telemetry must have a legitimate business need, clear disclosure, permissions, and retention controls.

## Development workflow

Large features use focused branches and meaningful commits. A feature is not complete until its backend/data/UI layers (where applicable), validation, permissions, error handling, tests, and documentation work together.

See [`docs/architecture.md`](docs/architecture.md) for the current architecture direction.
