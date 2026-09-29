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
- Source-controlled identity/authorization, Employee Core, and Attendance Core migrations.
- JWT access tokens with short lifetime.
- Opaque rotating refresh tokens; only SHA-256 token hashes are persisted.
- Role and permission entities stored in the database.
- Idempotent startup seeding for built-in roles/permissions after the schema exists.
- Permission claims and authorization policies.
- Login lockout and API rate limiting.
- Security, administration, shift, and attendance audit records.
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

## Database startup policy

Production should keep `Database:AutoMigrate=false` and apply committed migrations deliberately during deployment. After the schema is ready, API startup seeds missing built-in identity metadata and optionally creates the one-time bootstrap administrator when bootstrap credentials are supplied. Seeding is idempotent and does not replace existing role or permission assignments.

Local development may set `Database:AutoMigrate=true` so the API applies committed migrations before seeding.

## Security boundaries

Passwords are hashed with ASP.NET Core `PasswordHasher<TUser>`. JWT signing keys, database passwords, and bootstrap administrator credentials are configuration secrets and must not be committed. Role assignment is treated as a privilege-bearing operation rather than a normal profile edit.

Attendance self-service is server validated; the future desktop client will not be trusted to calculate late/early status or mutate attendance history directly. Administrative shift and attendance reads remain permission-controlled and auditable.

Sensitive monitoring features are intentionally outside the current foundation. If later approved, monitoring must remain transparent, business-scoped, permission-controlled, auditable, and subject to explicit retention rules. Hidden spyware behavior, keylogging, credential capture, covert camera/microphone use, and unrelated private-file collection remain out of scope.

## Database changes

Schema changes must be added as Entity Framework Core migrations and committed with the code that depends on them. Migration IDs must preserve dependency order. The current dependency order is identity foundation, Employee Core, then Attendance Core. The pinned `dotnet-ef` tool and CI `has-pending-model-changes` check prevent entity/model changes from silently drifting away from committed migrations, while the PostgreSQL migration test validates that the full chain applies correctly to a fresh database.
