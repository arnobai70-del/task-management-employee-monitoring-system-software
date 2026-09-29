# Task Management & Employee Monitoring System

A centralized, internet-required task management, attendance, survey operations, reporting, and transparent employee work-monitoring platform for development teams and survey/field teams.

## Status

Development is active. Implemented milestones now include:

- Secure ASP.NET Core / .NET 10 backend foundation.
- PostgreSQL 17 + Entity Framework Core migrations.
- JWT access tokens + rotating opaque refresh tokens.
- Database-backed roles and permissions.
- Employee Core: departments, employee provisioning, supervisors, roles, activation/deactivation, search and paging.
- Attendance Core: shifts, effective-dated assignments, check-in/out, breaks, late/early calculation and overnight-shift handling.
- Project / Task Core: projects, membership, task assignment, priorities, due dates, guarded status transitions, comments and activity history.
- Survey / Field Operations Core: forms, typed questions, field assignments, drafts/submission, immutable revisions and supervisor review.
- Reporting / Dashboard API Core: workforce, attendance, projects, tasks, employee workload and survey progress.
- Admin Web foundation: authenticated responsive React console with permission-aware navigation plus live Dashboard, Employees, Attendance, Projects, Tasks and Surveys read views.
- GitHub Actions quality gates for backend build/tests, EF migration drift, PostgreSQL integration paths and Admin Web TypeScript/Vite production builds.

Still pending: Admin Web write/management workflows, employee desktop client, background Windows service, realtime presence, notifications, approved transparent monitoring telemetry, installer and updater.

## Architecture

```text
Employee PC
  -> Desktop Client (planned)
  -> Background Monitoring Service (planned)
  -> Internet / HTTPS
  -> ASP.NET Core API
  -> PostgreSQL
  -> React Admin Web
```

Normal employee functionality is intentionally server/internet dependent. The product is not designed as an offline-first application.

## Technology baseline

- Backend: ASP.NET Core / .NET 10
- Database: PostgreSQL 17
- ORM: Entity Framework Core
- Authentication: JWT access tokens + rotating opaque refresh tokens
- Authorization: database-backed roles and permissions
- Admin Web: React 19 + TypeScript + Vite + React Router
- API documentation: OpenAPI
- Backend tests: xUnit v3 on Microsoft Testing Platform
- CI: GitHub Actions
- Planned desktop/background agent: C#/.NET Windows applications/services
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
  AdminWeb/
    src/
    package.json
    vite.config.ts
    tsconfig.json
tests/
  Backend.Tests/
docs/
  architecture.md
  admin-web.md
.github/workflows/
```

Backend business rules are authoritative and must not be duplicated or weakened by clients.

## Main API areas

- `/api/auth` — login, refresh and logout.
- `/api/departments`, `/api/employees`, `/api/roles` — organization and employee administration.
- `/api/shifts`, `/api/attendance` — shift and attendance workflows.
- `/api/projects`, `/api/tasks` — project/task operations.
- `/api/surveys`, `/api/survey-assignments`, `/api/survey-submissions` — field survey and review workflows.
- `/api/reports/*` — read-only dashboard/reporting endpoints.
- `/health` — API health endpoint.
- `/openapi/v1.json` — OpenAPI document.

Permissions are enforced server-side. Important current permission families include `employees.*`, `departments.*`, `roles.*`, `shifts.*`, `attendance.*`, `projects.*`, `tasks.*`, survey permissions, `reports.read`, and `audit.read`.

## Admin Web

The current browser console lives in `src/AdminWeb`.

Implemented routes:

- `/dashboard` — requires `reports.read`.
- `/employees` — requires `employees.read`.
- `/attendance` — requires `attendance.read`.
- `/projects` — requires `projects.read`.
- `/tasks` — requires `tasks.read`.
- `/surveys` — requires `surveys.read`.

Navigation is generated from server-issued permission claims. The browser uses those claims only for presentation/route guarding; backend authorization remains authoritative for every API request.

The current auth contract returns refresh tokens in JSON. Admin Web therefore stores the browser session in `sessionStorage` rather than persistent `localStorage`, rotates refresh tokens through `/api/auth/refresh`, retries one authorized request after refresh, and clears the session when refresh fails.

Run the Admin Web locally:

```bash
cd src/AdminWeb
npm install
npm run dev
```

Vite listens on port `5173` and proxies `/api` to `http://localhost:5080` by default. Override the local target when needed:

```bash
VITE_DEV_API_TARGET=https://localhost:7001 npm run dev
```

For a production build:

```bash
cd src/AdminWeb
npm install --no-audit --no-fund
npm run build
```

The production output is written to `src/AdminWeb/dist`. See [`docs/admin-web.md`](docs/admin-web.md) for session, deployment and permission details.

## Backend local prerequisites

- .NET 10 SDK
- Docker Desktop or local PostgreSQL 17
- Git

Copy `.env.example` to `.env` for Docker-oriented configuration and replace all placeholder secrets. Never commit `.env`.

Required API configuration:

- `ConnectionStrings__DefaultConnection`
- `Jwt__SigningKey` — at least 32 bytes of random secret material

Optional one-time bootstrap administrator configuration:

- `BootstrapAdmin__Email`
- `BootstrapAdmin__Password` — at least 12 characters

Remove bootstrap credentials after the initial administrator exists.

## Start PostgreSQL

```bash
cp .env.example .env
# Edit .env and replace placeholder passwords/keys.
docker compose up -d postgres
```

## Database migrations

```bash
dotnet tool restore
dotnet ef database update \
  --project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj \
  --startup-project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj
```

Production should apply committed migrations deliberately and keep `Database__AutoMigrate=false`. Local development may use `Database__AutoMigrate=true`.

CI rejects EF model changes that do not have a matching committed migration and validates the full migration chain against PostgreSQL.

## Run the API

```bash
dotnet restore TaskMonitoring.slnx
dotnet run --project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj
```

## Validation

Backend validation commands:

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

Admin Web validation:

```bash
cd src/AdminWeb
npm install --no-audit --no-fund
npm run build
```

The PostgreSQL integration test expects `TEST_POSTGRES_CONNECTION` to point to an isolated test database. GitHub Actions supplies one automatically.

## Security and monitoring principles

- No plain-text passwords or committed production secrets.
- Server-side authentication, authorization and validation are mandatory.
- Browser permission checks are UX controls only; they never replace backend policies.
- Refresh tokens are random opaque values and only their SHA-256 hashes are persisted server-side.
- Admin Web does not persist its current session in `localStorage`.
- Deactivating an employee disables the linked account and revokes active refresh tokens.
- Organization-wide attendance, project/task, survey, reporting, role and audit access require explicit permissions.
- Important authentication and business mutations are audit logged.
- Hidden spyware behavior, keylogging, password capture, covert camera/microphone activation and unrelated private-file collection are explicitly out of scope.
- Any future screenshot, app-usage, URL or location telemetry must have a legitimate business need, clear employee disclosure, explicit authorization boundaries, auditability and retention controls.

## Development workflow

Large features use focused branches and meaningful commits. A feature is complete only when its applicable API/data/UI layers, validation, permissions, error handling, tests/CI gates and documentation work together.

See [`docs/architecture.md`](docs/architecture.md) for architecture direction and [`docs/admin-web.md`](docs/admin-web.md) for Admin Web details.
