# Task Management & Employee Monitoring System

A centralized, internet-required task management, attendance, reporting, access-assignment, and transparent employee work platform for office teams.

## Status

Development is active. Implemented milestones now include:

- Secure ASP.NET Core / .NET 10 backend foundation.
- PostgreSQL 17 + Entity Framework Core migrations.
- JWT access tokens + rotating opaque refresh tokens.
- Database-backed roles and permissions.
- Employee Core: departments, employee provisioning, supervisors, roles, activation/deactivation, search and paging.
- Attendance Core: shifts, effective-dated assignments, check-in/out, breaks, late/early calculation and overnight-shift handling.
- Project / Task Core: projects, membership, task assignment, priorities, due dates, guarded status transitions, comments and activity history.
- Survey / Field Operations backend core: forms, typed questions, field assignments, drafts/submission, immutable revisions and supervisor review.
- Reporting / Dashboard API Core: workforce, attendance, projects, tasks, employee workload and survey progress.
- RDP/IP/Website access assignment backend and Admin Web workflows with dedicated permissions and audit history.
- Admin Web: authenticated responsive React console with permission-aware dashboard and employee/department/shift/project/task/access management workflows. Survey management write UI is intentionally not part of the current Admin Web roadmap.
- Employee Windows desktop foundation: sign-in, attendance actions, assigned tasks, assigned RDP/IP/Website access, rotating refresh-token session handling and visible privacy disclosure.
- Employee Windows service foundation: visible Windows Service-compatible server reachability/service-health heartbeat only.
- Explicit PowerShell publish/install/uninstall scripts for the employee Windows service.
- GitHub Actions quality gates for backend build/tests, EF migration drift, PostgreSQL integration, Admin Web builds, and Windows desktop/service build + publish validation.

Still pending: realtime presence, notifications, additional approved transparent monitoring telemetry, production-grade installer packaging/updater, code signing, deployment automation, and final operational hardening.

## Architecture

```text
Employee PC
  -> .NET 10 WPF Employee Desktop
  -> Visible .NET Windows Service
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
- Employee Desktop: .NET 10 WPF (`net10.0-windows`)
- Employee Background Service: .NET 10 Worker + Windows Services integration
- API documentation: OpenAPI
- Backend tests: xUnit v3 on Microsoft Testing Platform
- CI: GitHub Actions on Linux and Windows runners
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
  EmployeeDesktop/
    TaskMonitoring.EmployeeDesktop/
  EmployeeService/
    TaskMonitoring.EmployeeService/
scripts/
  windows/
    publish-employee-windows.ps1
    install-employee-service.ps1
    uninstall-employee-service.ps1
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
- `/api/me/tasks` — authenticated employee's own assigned tasks; employee identity is resolved from the JWT subject server-side.
- `/api/me/access` — authenticated employee's own RDP/IP/Website assignments; employee identity is resolved server-side.
- `/api/access-assignments/*` — admin RDP/IP/Website assignment workflows.
- `/api/surveys`, `/api/survey-assignments`, `/api/survey-submissions` — field survey backend workflows.
- `/api/reports/*` — read-only dashboard/reporting endpoints.
- `/health` — API health endpoint.
- `/openapi/v1.json` — OpenAPI document.

Permissions are enforced server-side. Organization-wide permission families include `employees.*`, `departments.*`, `roles.*`, `shifts.*`, `attendance.*`, `projects.*`, `tasks.*`, survey permissions, `access.assignments.*`, `reports.read`, and `audit.read`. The `/api/me/*` endpoints are self-scoped and do not grant organization-wide access.

## Admin Web

The browser console lives in `src/AdminWeb`.

Current management areas include dashboard/reporting, employees, departments, attendance/shifts, projects, tasks, RDP assignments, IP assignments and website assignments. Navigation and write controls are generated from server-issued permission claims; backend authorization remains authoritative for every API request.

The current auth contract returns refresh tokens in JSON. Admin Web stores the browser session in `sessionStorage` rather than persistent `localStorage`, rotates refresh tokens through `/api/auth/refresh`, retries one authorized request after refresh, and clears the session when refresh fails.

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

## Employee Windows desktop and service

The WPF employee client lives at `src/EmployeeDesktop/TaskMonitoring.EmployeeDesktop`. It currently provides:

- employee sign-in;
- current attendance status;
- check-in, break start/end and check-out;
- self-scoped assigned task list;
- self-scoped RDP/IP/Website assignment views;
- automatic access-token refresh using rotating refresh tokens;
- explicit logout/revocation;
- visible monitoring/privacy disclosure.

Desktop access and refresh tokens are kept only in process memory in the current implementation. They are not written to plaintext files or registry values. Restarting the desktop application requires sign-in again. A normal sign-out revokes the current refresh token; the window also attempts logout before closing.

The Windows background worker lives at `src/EmployeeService/TaskMonitoring.EmployeeService`. Its current scope is deliberately narrow: configurable server reachability and service-health heartbeat logging. It does not capture keys, passwords, screenshots, audio/video or unrelated files.

Publish both Windows applications from an elevated or normal PowerShell session with .NET 10 installed:

```powershell
./scripts/windows/publish-employee-windows.ps1
```

By default this creates framework-dependent `win-x64` output under `artifacts/employee-windows/desktop` and `artifacts/employee-windows/service`.

Install the visible Windows service from an elevated PowerShell session:

```powershell
./scripts/windows/install-employee-service.ps1 `
  -ServiceDirectory "C:\Program Files\TaskMonitoring\EmployeeService" `
  -ServerUrl "https://your-company-api.example.com" `
  -HeartbeatSeconds 60
```

The install script writes only `ServerUrl`, heartbeat interval and logging configuration. It does not write passwords, JWTs or refresh tokens. Uninstall with:

```powershell
./scripts/windows/uninstall-employee-service.ps1
```

The uninstall script intentionally leaves published files in place so removal of application data/binaries remains an explicit administrator decision. MSI/MSIX packaging, code signing and automatic update delivery are still pending.

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

Employee Windows validation runs on a Windows machine/runner:

```powershell
dotnet build src/EmployeeDesktop/TaskMonitoring.EmployeeDesktop/TaskMonitoring.EmployeeDesktop.csproj --configuration Release
dotnet build src/EmployeeService/TaskMonitoring.EmployeeService/TaskMonitoring.EmployeeService.csproj --configuration Release
./scripts/windows/publish-employee-windows.ps1
```

The PostgreSQL integration test expects `TEST_POSTGRES_CONNECTION` to point to an isolated test database. GitHub Actions supplies one automatically.

## Security and monitoring principles

- No plain-text passwords or committed production secrets.
- Server-side authentication, authorization and validation are mandatory.
- Browser/desktop presentation checks never replace backend policies.
- Refresh tokens are random opaque values and only their SHA-256 hashes are persisted server-side.
- Admin Web does not persist its current session in `localStorage`.
- Employee Desktop currently keeps access/refresh tokens only in process memory and performs refresh-token rotation.
- Deactivating an employee disables the linked account and revokes active refresh tokens.
- Organization-wide attendance, project/task, survey, access-assignment, reporting, role and audit access require explicit permissions.
- Employee self-workspace endpoints derive employee identity from the authenticated account rather than accepting an employee ID from the client.
- Important authentication and business mutations are audit logged.
- Hidden spyware behavior, keylogging, password capture, covert screenshots, camera/microphone activation and unrelated private-file collection are explicitly out of scope.
- Any future screenshot, app-usage, URL or location telemetry must have a legitimate business need, clear employee disclosure, explicit authorization boundaries, auditability and retention controls.

## Development workflow

Large features use focused branches and meaningful commits. A feature is complete only when its applicable API/data/UI layers, validation, permissions, error handling, tests/CI gates and documentation work together.

See [`docs/architecture.md`](docs/architecture.md) for architecture direction and [`docs/admin-web.md`](docs/admin-web.md) for Admin Web details.
