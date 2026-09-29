# Architecture

## Direction

The product is a client-server system. Employee desktop software requires a valid connection to the central API for normal working functionality. The backend is shared by the future Windows desktop client, Windows background service, and React admin dashboard.

## Repository layout

- `src/Backend/TaskMonitoring.Api` — ASP.NET Core API and business services.
- `src/Backend/TaskMonitoring.Api/Migrations` — source-controlled Entity Framework Core migrations and model snapshot.
- `tests/Backend.Tests` — backend unit/business-rule tests plus PostgreSQL migration/seed validation.
- `docs` — architecture and operational documentation.
- Future modules will add `src/AdminWeb`, `src/DesktopClient`, and `src/DesktopService` without duplicating backend business rules.

## Backend foundation

The backend starts as a modular monolith so deployment, authorization, and transactions stay simple while the domain is evolving. The current foundation includes:

- PostgreSQL + Entity Framework Core.
- Source-controlled identity/authorization, Employee Core, Attendance Core, Project / Task Core, and Survey / Field Operations Core migrations.
- JWT access tokens with short lifetime.
- Opaque rotating refresh tokens; only SHA-256 token hashes are persisted.
- Role and permission entities stored in the database.
- Idempotent startup seeding for built-in roles/permissions after the schema exists.
- Permission claims and authorization policies.
- Login lockout and API rate limiting.
- Security, administration, attendance, project, task, and survey audit records.
- Central exception handling with safe user-facing errors.
- Health and OpenAPI endpoints.
- CI checks for Release build warnings/errors, EF model drift, business rules, and the complete migration chain against PostgreSQL.

## Employee Core

Employee Core owns the initial organization and employee identity layer:

- `Department` stores a stable code, display name, activation state, and timestamps.
- `Employee` is a one-to-one profile attached to a `User` account.
- Employees can belong to a department and optionally report to another employee.
- Supervisor assignments are validated so a reporting cycle cannot be created.
- Employee codes and user emails are normalized and unique.
- Employee provisioning creates the user account, hashes the initial password, creates the employee profile, and applies selected roles in one database unit of work.
- Employee updates can change organization/profile data and role membership without destructive account replacement.
- Employee deactivation disables both the employee and linked user account and revokes active refresh tokens while preserving historical references.
- An authenticated employee cannot use the Employee Core update path to deactivate their own account or change their own role membership.

Department and employee read/write operations use dedicated permissions. Employee create/update currently carries role assignments, so write endpoints require both `employees.manage` and `roles.manage`. This is an intentional privilege boundary: possession of employee-management authority alone must not grant the ability to assign privileged roles.

Hard delete is intentionally absent from Employee Core. Attendance, task, survey, and reporting records need stable historical employee references.

## Attendance / Shift / Work Session Core

Attendance Core establishes the server-side work-time state machine that future desktop and admin clients will consume.

### Shift model

- `Shift` stores a stable code/name, local start/end times, IANA timezone ID, grace minutes, active state, and timestamps.
- Equal start/end times are rejected; overnight shifts are represented by an end time that is earlier than the start time.
- `EmployeeShiftAssignment` binds an active employee to an active shift for an effective date range.
- Overlapping assignment ranges for the same employee are rejected by service validation.
- Missing effective-from dates are rejected rather than silently using `0001-01-01`.

All event timestamps are persisted in UTC. Scheduled UTC boundaries are derived from the shift's local times and configured timezone. This keeps schedule evaluation independent of the API host and employee device timezone. For overnight shifts, after-midnight activity maps back to the shift's starting work date.

### Work-session model

- `WorkSession` captures employee, shift, assignment, work date, scheduled UTC boundaries, actual check-in/check-out timestamps, late minutes, early-leave minutes, and accumulated break minutes.
- `(EmployeeId, WorkDate)` is unique, preventing duplicate attendance records for the same work date.
- The service also blocks a new check-in while any prior session is still open, so employees must resolve an older session before starting another work date.
- `WorkBreak` captures explicit break start/end timestamps and calculated duration.
- A filtered unique database index allows only one open break per work session.
- Checkout is rejected while a break is open; breaks are never silently auto-closed.

The employee status endpoint checks for an already-open session before resolving the current shift. This preserves accurate `Working` or `OnBreak` state even if the open session belongs to a previous work date.

### Authorization boundaries

- `shifts.read` permits shift and assignment lookup.
- `shifts.manage` permits shift creation/update and employee shift assignment.
- `attendance.read` permits organization-wide attendance/work-session history.
- Employee self-service status/check-in/break/check-out endpoints require authentication and an active employee profile, but do not grant organization-wide attendance visibility.

Important shift and attendance lifecycle operations are audit logged with the acting user, target record, request context, and relevant event metadata.

## Project / Task Core

Project / Task Core establishes the collaborative work model used by future admin and employee clients.

### Project model

- `Project` stores a unique normalized code, name, description, lifecycle status, optional start/due dates, and timestamps.
- Project statuses are `Planning`, `Active`, `OnHold`, `Completed`, and `Archived`.
- New projects cannot begin in `Completed` or `Archived` state.
- A project cannot move to `Completed` or `Archived` while it still has tasks outside `Done` or `Cancelled`.
- Archived projects are immutable through Project / Task Core service paths.
- Project summaries calculate active-member and open-task counts in translated database projections rather than relying on loaded navigation collections.

`ProjectMember` preserves membership history instead of deleting rows. A unique `(ProjectId, EmployeeId)` record can be reactivated and carries either `Member` or `Manager` role. Only active employees may join projects. Removing a member is blocked while that employee owns an open task, preventing orphaned active assignments.

### Task model and workflow

- `ProjectTask` stores project, title, description, priority, status, optional assignee, optional due date, creator, completion timestamp, and audit timestamps.
- Priorities are `Low`, `Normal`, `High`, and `Urgent`.
- Assignees must be active employees and active project members.
- Task due dates must stay within configured project start/due boundaries.
- New tasks cannot be added to completed or archived projects.
- Status transitions are explicit: `ToDo -> InProgress/Blocked/Cancelled`, `InProgress -> Blocked/Done/Cancelled`, `Blocked -> InProgress/Cancelled`, `Done -> InProgress`, and `Cancelled -> ToDo`.
- Entering `Done` records `CompletedAtUtc`; reopening clears it.

`TaskComment` is append-only user-authored discussion. `TaskActivity` is append-only structured history for creation, edits, status changes, and comments. These records support future audit views and activity feeds without mutating historical events.

### Authorization boundaries

- `projects.read` permits project and membership lookup.
- `projects.manage` permits project creation/update and membership management.
- `tasks.read` permits task, comment, and activity lookup.
- `tasks.manage` permits task creation, edits, assignment, and status transitions.
- `tasks.comment` permits comment creation; the current controller also requires `tasks.read`, so comment writers can only comment where they can read task context.

Project/member/task mutations also write global audit events with actor, target, request context, and relevant metadata.

## Survey / Field Operations Core

Survey / Field Operations Core adds a project-scoped questionnaire and supervised field-data workflow without allowing published questionnaires or reviewed history to be silently rewritten.

### Form and question model

- `SurveyForm` belongs to a project and stores unique code, name, description, lifecycle status, and timestamps.
- Lifecycle states are `Draft`, `Published`, `Closed`, and `Archived`.
- Forms can be edited only while `Draft`.
- Publishing requires at least one question; once published, questionnaire structure is locked.
- `SurveyQuestion` stores a stable per-form key, prompt, type, required flag, choice options as JSON, and explicit sort order.
- Supported question types are `Text`, `LongText`, `Number`, `Boolean`, `Date`, `SingleChoice`, and `MultipleChoice`.
- Choice questions require unique configured options; non-choice questions cannot carry choice options.

Question keys and sort positions are unique within a form. Locking the questionnaire after publication ensures stored answers retain a stable interpretation throughout later review and reporting.

### Assignment and submission model

- `SurveyAssignment` binds a published form to one active employee with an optional due date and workflow status.
- `(SurveyFormId, EmployeeId)` is unique, preventing duplicate concurrent/historical assignment rows for the same employee and survey.
- The employee self-service path resolves the authenticated user to an active employee profile and rejects attempts to mutate another employee's assignment.
- `SurveySubmission` belongs to an assignment and stores a monotonically increasing revision number, draft/submitted/approved/rejected status, submission timestamp, review metadata, and timestamps.
- `(SurveyAssignmentId, RevisionNumber)` is unique.
- `SurveyAnswer` stores one JSON value per question per revision; `(SurveySubmissionId, SurveyQuestionId)` is unique.

Draft saves validate referenced questions and answer data types but do not require every required question yet. Final submit enforces required answers, scalar/array type semantics, date format, and configured choices. Submitted revisions are locked from employee editing. If a reviewer rejects a revision, resubmission creates a new revision and leaves the rejected response intact. Approved revisions remain immutable.

### Review workflow

- Reviewers can list pending submissions separately from field-assignment administration.
- A reviewer may `Approve` or `Reject` only a currently submitted revision.
- Rejection requires a review comment; approval may have an optional comment.
- Review updates both submission and assignment status atomically through the service flow.
- A survey cannot move to `Closed` or `Archived` while a submission is still waiting for review.

### Authorization boundaries

- `surveys.read` permits form/questionnaire lookup.
- `surveys.manage` permits form creation, draft edits, questionnaire replacement, publishing, closing, and archiving.
- `survey.assignments.read` permits organization-wide field-assignment lookup.
- `survey.assignments.manage` permits assignment and eligible cancellation.
- `survey.submit` permits employee-owned assignment lookup, draft save, and submission.
- `survey.review` permits pending-response lookup and approve/reject decisions.

These capabilities are independent permission boundaries. The built-in permission catalog and SuperAdmin seeding include them, but operational roles such as surveyor/supervisor should receive only the specific survey permissions intentionally assigned by administrators. Survey lifecycle, assignment, draft/submit, and review mutations also write global audit events.

## Database startup policy

Production should keep `Database:AutoMigrate=false` and apply committed migrations deliberately during deployment. After the schema is ready, API startup seeds missing built-in identity metadata and optionally creates the one-time bootstrap administrator when bootstrap credentials are supplied. Seeding is idempotent and does not replace existing role or permission assignments.

Local development may set `Database:AutoMigrate=true` so the API applies committed migrations before seeding.

## Security boundaries

Passwords are hashed with ASP.NET Core `PasswordHasher<TUser>`. JWT signing keys, database passwords, and bootstrap administrator credentials are configuration secrets and must not be committed. Role assignment is treated as a privilege-bearing operation rather than a normal profile edit.

Attendance self-service is server validated; the future desktop client will not be trusted to calculate late/early status or mutate attendance history directly. Administrative shift and attendance reads remain permission-controlled and auditable.

Project and task lifecycle rules are also server validated. Clients cannot bypass active project membership for task assignment, arbitrary task status transitions, open-task project completion guards, or open-assignment member-removal guards.

Survey form, assignment, submission, and review rules are server validated. Field workers cannot submit against another employee's assignment, cannot submit to non-published forms, and cannot overwrite submitted/approved revisions. Reviewer authority is a separate permission from assignment administration and employee submission authority.

Sensitive monitoring features are intentionally outside the current foundation. If later approved, monitoring must remain transparent, business-scoped, permission-controlled, auditable, and subject to explicit retention rules. Hidden spyware behavior, keylogging, credential capture, covert camera/microphone use, and unrelated private-file collection remain out of scope.

## Database changes

Schema changes must be added as Entity Framework Core migrations and committed with the code that depends on them. Migration IDs must preserve dependency order. The current dependency order is identity foundation, Employee Core, Attendance Core, Project / Task Core (`20260929070000_ProjectTaskCore`), then Survey / Field Operations Core (`20260929080000_SurveyFieldOperationsCore`). The pinned `dotnet-ef` tool and CI `has-pending-model-changes` check prevent entity/model changes from silently drifting away from committed migrations, while the PostgreSQL migration test validates that the full chain applies correctly to a fresh database and accepts representative identity, employee, attendance, project/task, and survey form/question/assignment/submission/answer records.
