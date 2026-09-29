# Architecture

## Direction

The product is a client-server system. Normal employee functionality requires a valid connection to the central API. The backend is shared by the future Windows desktop client, Windows background service, and React admin dashboard.

The backend remains a modular monolith while the domain is evolving. This keeps deployment, authorization, transaction boundaries, and schema management simple without duplicating business rules across clients.

## Repository layout

- `src/Backend/TaskMonitoring.Api` — ASP.NET Core API, domain models, contracts, controllers, and business services.
- `src/Backend/TaskMonitoring.Api/Migrations` — source-controlled Entity Framework Core migrations and model snapshot.
- `tests/Backend.Tests` — business-rule tests plus real PostgreSQL migration/query validation.
- `docs` — architecture and operational documentation.
- Future modules will add `src/AdminWeb`, `src/DesktopClient`, and `src/DesktopService`.

## Backend foundation

The current foundation includes:

- PostgreSQL + Entity Framework Core.
- JWT access tokens with rotating opaque refresh tokens; only SHA-256 refresh-token hashes are persisted.
- Database-backed roles and permissions.
- Idempotent startup seeding for built-in roles/permissions after the schema exists.
- Permission claims and authorization policies.
- Login lockout and API rate limiting.
- Security and operational audit records.
- Central exception handling with safe user-facing errors.
- Health and OpenAPI endpoints.
- CI checks for warning-free Release builds, EF model drift, business rules, and PostgreSQL migration/query compatibility.

## Employee Core

Employee Core owns organization and employee identity:

- `Department` stores stable code/name, activation state, and timestamps.
- `Employee` is a one-to-one profile attached to a `User` account.
- Employees may belong to a department and optionally report to another employee.
- Reporting-line cycles are rejected.
- Employee codes and user emails are normalized and unique.
- Provisioning creates the user, hashes the initial password, creates the employee profile, and applies selected roles in one database unit of work.
- Deactivation disables both employee and user, revokes active refresh tokens, and preserves historical references.
- Self-deactivation and self-role mutation are blocked through the employee administration path.

Employee create/update can carry role assignments, so those paths require both employee-management and role-management authority. Hard delete is intentionally absent because attendance, task, survey, audit, and reporting records depend on stable employee references.

## Attendance / Shift / Work Session Core

### Shift model

- `Shift` stores code/name, local start/end times, IANA timezone ID, grace minutes, active state, and timestamps.
- Equal start/end times are rejected; an earlier end time represents an overnight shift.
- `EmployeeShiftAssignment` binds an active employee to an active shift for an effective date range.
- Overlapping assignment ranges for the same employee are rejected.

All event timestamps are stored in UTC. Scheduled UTC boundaries are derived from the shift's local times and configured timezone. Overnight activity after midnight maps back to the shift's starting work date.

### Work-session model

- `WorkSession` captures employee, shift, assignment, work date, scheduled/actual timestamps, late minutes, early-leave minutes, and total break minutes.
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

- `Project` stores unique code/name, description, lifecycle status, optional start/due dates, and timestamps.
- Statuses: `Planning`, `Active`, `OnHold`, `Completed`, `Archived`.
- A project cannot complete/archive while open tasks remain.
- Archived projects are immutable through the Project / Task service paths.
- `ProjectMember` preserves membership history with `Member` or `Manager` role and active/inactive state.
- Removing a project member is blocked while that employee owns an open task.

### Task model

- `ProjectTask` stores project, title/description, priority, status, optional assignee/due date, creator, completion timestamp, and audit timestamps.
- Assignees must be active employees and active project members.
- Task due dates must stay inside configured project date boundaries.
- Explicit transitions prevent arbitrary state jumps.
- `TaskComment` is append-only discussion.
- `TaskActivity` is append-only structured history for creation, edit, status change, and comments.

### Authorization

- `projects.read` / `projects.manage`
- `tasks.read` / `tasks.manage`
- `tasks.comment`

## Survey / Field Operations Core

### Form and question model

- `SurveyForm` belongs to a project and has `Draft`, `Published`, `Closed`, or `Archived` state.
- Forms/questions are editable only while draft.
- Publishing requires at least one question and locks questionnaire structure.
- `SurveyQuestion` supports text, long text, number, boolean, date, single choice, and multiple choice.
- Choice options, question keys, required flags, and sort positions are validated server-side.

### Assignment and submission model

- `SurveyAssignment` binds a published form to an active employee with optional due date and workflow state.
- `(SurveyFormId, EmployeeId)` is unique.
- Employee self-service resolves the authenticated user to an active employee and rejects cross-employee mutation.
- `SurveySubmission` keeps monotonically increasing revision numbers.
- `SurveyAnswer` stores one JSON value per question per revision.
- Drafts validate referenced questions/types; final submission also enforces required answers.
- Submitted revisions are immutable.
- Rejection creates a later revision instead of overwriting history.
- Approved revisions remain immutable.

### Review and authorization

- Reviewers may approve/reject only submitted revisions.
- Rejection requires a comment.
- Review updates assignment and submission state through one service flow.
- Closing/archiving is blocked while a submission awaits review.

Permissions are separated into `surveys.read`, `surveys.manage`, `survey.assignments.read`, `survey.assignments.manage`, `survey.submit`, and `survey.review`.

## Reporting / Dashboard API Core

Reporting Core is deliberately **read-only**. It queries existing operational tables and returns derived projections; there are no reporting entities, materialized reporting tables, or cache tables in this phase. This avoids stale duplicated state while the data model is still evolving.

### Dashboard overview

`GET /api/reports/dashboard` aggregates:

- active employee and department counts;
- attendance session, employee, completion, open-session, late, early-leave, and break totals for a bounded date range;
- project counts by lifecycle state plus overdue projects;
- task counts by workflow state, overdue/unassigned open tasks, and completions inside the date window;
- survey published-form, active-assignment, pending-review, approval/rejection, overdue, submitted, and reviewed metrics.

The default range is the latest 30 days. Explicit ranges longer than 366 days are rejected to prevent accidental unbounded operational-table scans.

### Attendance trend

`GET /api/reports/attendance/daily` returns one row per requested date, including zero-filled dates with no work sessions. It may filter by department.

The department relationship is evaluated from the employee's **current** department because the current attendance schema does not store a historical department snapshot on each work session. Historical organization-chart reporting must be modeled explicitly if required later; the service does not pretend the current department is historical truth.

### Project progress

`GET /api/reports/projects` reports active members and task totals by project, including open, done, blocked, cancelled, and overdue counts. Completion percentage uses:

```text
Done / (Total - Cancelled) * 100
```

Cancelled tasks are excluded from the denominator. A project with no executable tasks reports 0% rather than inventing completion.

### Employee workload

`GET /api/reports/workload` considers active employees only and combines:

- open task count;
- urgent open task count;
- overdue task count;
- survey assignments not yet approved/cancelled;
- overdue active survey assignments.

Rows are ordered by total open workload and bounded by a server-clamped limit of 1–100.

### Survey progress

`GET /api/reports/surveys` reports assignment counts by state plus overdue assignments and approval percentage. Approval percentage uses approved assignments divided by total assignments excluding cancelled assignments.

### Authorization and query behavior

All Reporting Core endpoints require `reports.read`. This is intentionally separate from `attendance.read`, `projects.read`, `tasks.read`, and survey permissions because organization-wide aggregated reporting can expose broader operational context than an individual source screen.

Pure report reads do not create audit rows in this phase; they are non-mutating queries. If report-access auditing becomes a compliance requirement, it should be introduced explicitly rather than mixed into aggregation queries.

The service uses `AsNoTracking` queries and bounded filters. Representative reporting queries are executed through the real Npgsql/PostgreSQL provider in CI so translation behavior is tested in addition to EF InMemory business-rule tests.

## Database startup policy

Production should keep `Database:AutoMigrate=false` and apply committed migrations deliberately during deployment. After schema deployment, API startup idempotently seeds built-in identity metadata and optionally creates the one-time bootstrap administrator when bootstrap credentials are supplied.

Local development may set `Database:AutoMigrate=true` to apply committed migrations before seeding.

## Security boundaries

Passwords are hashed with ASP.NET Core `PasswordHasher<TUser>`. JWT signing keys, database passwords, and bootstrap credentials are configuration secrets and must not be committed.

Attendance calculations, project/task lifecycle rules, survey submission/review rules, and reporting range/aggregation semantics are all server-controlled. Clients are presentation/interaction layers and must not be trusted to redefine those rules.

`reports.read` is an organization-wide information boundary. Possession of a narrow operational permission does not automatically grant Reporting Core access.

Sensitive monitoring features remain outside the current foundation. If later approved, monitoring must be transparent, business-scoped, permission-controlled, auditable, and subject to explicit retention rules. Hidden spyware behavior, keylogging, credential capture, covert camera/microphone use, and unrelated private-file collection remain out of scope.

## Database changes

Schema changes must be added as EF Core migrations and committed with the code that depends on them. Current migration order is:

1. `20260929061000_InitialIdentityFoundation`
2. `20260929062000_EmployeeCore`
3. `20260929063000_AttendanceCore`
4. `20260929070000_ProjectTaskCore`
5. `20260929080000_SurveyFieldOperationsCore`

Reporting / Dashboard API Core adds no EF entities or mappings, so it requires **no new migration**. CI's `has-pending-model-changes` gate verifies that this remains true. The PostgreSQL integration test applies the full migration chain, seeds foundation metadata, writes representative identity/employee/attendance/project/task/survey records, and then executes Reporting Core queries using Npgsql.
