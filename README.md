# Task Management & Employee Monitoring System

A centralized, internet-required task management, attendance, survey operations, reporting, access-assignment, and transparent employee work-status platform for development teams and survey/field teams.

## Status

Development is active. Implemented milestones now include:

- Secure ASP.NET Core / .NET 10 backend foundation.
- PostgreSQL 17 + Entity Framework Core migrations.
- JWT access tokens + rotating opaque refresh tokens.
- Database-backed roles and permissions.
- Employee Core: departments, employee provisioning, supervisors, roles, activation/deactivation, search and paging.
- Attendance Core: shifts, effective-dated assignments, check-in/out, breaks, late/early calculation and overnight-shift handling.
- Project / Task Core: projects, membership, task assignment, priorities, due dates, guarded status transitions, comments and activity history.
- Survey / Field Operations Core: forms, typed questions, field assignments, drafts/submission, immutable revisions and supervisor review APIs.
- Reporting / Dashboard API Core: workforce, attendance, projects, tasks, employee workload and survey progress.
- Admin Web: authenticated responsive React console with permission-aware navigation; management workflows for employees, departments, shifts, projects, project membership, tasks, and RDP/IP/website assignments; Survey UI remains read-only in the current console.
- Access Assignment Core: audited RDP/IP/website assignment management without storing reusable passwords, private keys or session cookies.
- Employee Desktop Client: Windows employee login, secure rotating session persistence, attendance controls, assigned task/access views, visible heartbeat/status, explicit privacy disclosure and opt-in per-user Windows startup.
- Employee Windows Agent: credential-free backend health/connectivity service separated from employee authentication and activity data.
- GitHub Actions quality gates for backend build/tests, EF migration drift, PostgreSQL integration paths, Admin Web production builds, and Windows employee client/agent builds and tests.

Still pending for later milestones: dedicated Admin Web realtime/presence screen, Survey management write UI, notifications, approved additional transparent telemetry (if a legitimate requirement is defined), signed installer package, automatic updater, and production deployment hardening.

## Architecture

```text
Employee PC
  -> Employee Desktop Client
       -> Login / rotating refresh token protected with Windows DPAPI
       -> Attendance self-service
       -> Assigned tasks and business access
       -> Visible authenticated heartbeat
  -> Windows Employee Agent
       -> Credential-free backend /health connectivity check only
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
- Employee Desktop: C# / .NET 10 Windows Forms
- Windows Agent: .NET 10 Worker/Windows Service
- Employee desktop local secret protection: Windows DPAPI, current-user scope
- API documentation: OpenAPI
- Backend/client tests: xUnit v3 on Microsoft Testing Platform
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
    TaskMonitoring.Employee.Shared/
    TaskMonitoring.EmployeeDesktop/
  EmployeeAgent/
    TaskMonitoring.EmployeeAgent/
tests/
  Backend.Tests/
  EmployeeClient.Tests/
docs/
  architecture.md
  admin-web.md
  employee-desktop.md
scripts/
  publish-employee-client.ps1
  install-employee-agent.ps1
  uninstall-employee-agent.ps1
.github/workflows/
```

Backend business rules are authoritative and must not be duplicated or weakened by clients.

## Main API areas

- `/api/auth` — login, refresh and logout.
- `/api/departments`, `/api/employees`, `/api/roles` — organization and employee administration.
- `/api/shifts`, `/api/attendance` — shift and attendance workflows.
- `/api/projects`, `/api/tasks` — project/task operations.
- `/api/access-assignments/*` — RDP, IP and website assignment administration.
- `/api/desktop/me` — self-scoped employee desktop dashboard.
- `/api/desktop/me/heartbeat` — minimal authenticated employee desktop heartbeat.
- `/api/desktop/presence` — permission-protected employee presence view API.
- `/api/surveys`, `/api/survey-assignments`, `/api/survey-submissions` — field survey and review workflows.
- `/api/reports/*` — read-only dashboard/reporting endpoints.
- `/health` — API health endpoint.
- `/openapi/v1.json` — OpenAPI document.

Permissions are enforced server-side. Important current permission families include `employees.*`, `departments.*`, `roles.*`, `shifts.*`, `attendance.*`, `projects.*`, `tasks.*`, `access.assignments.*`, survey permissions, `reports.read`, and `audit.read`.

## Admin Web

The browser console lives in `src/AdminWeb`.

Current navigation covers Dashboard, Employees, Attendance, Projects, Tasks, Surveys, RDP Assign, IP Assign and Website Access according to server-issued permissions. Employee/department/shift/project/task/access write controls are permission-aware and backend authorization remains authoritative for every action. Survey management write UI is intentionally deferred; the current Survey screen remains read-only.

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

See [`docs/admin-web.md`](docs/admin-web.md) for session, deployment and permission details.

## Employee Desktop and Windows Agent

The Windows desktop client is in `src/EmployeeDesktop/TaskMonitoring.EmployeeDesktop`. It supports employee sign-in, attendance self-actions, assigned tasks and access information, a visible authenticated heartbeat, and a Privacy & Status screen that tells the employee exactly what this phase does and does not collect.

Access tokens stay in memory. The rotating refresh token is encrypted using Windows DPAPI with `DataProtectionScope.CurrentUser` and stored under the current user's local application-data folder. Employee passwords are never persisted.

The separate `TaskMonitoring.EmployeeAgent` Windows service receives no employee password, access token or refresh token. It checks only the backend `/health` endpoint and logs connectivity state changes. It does not launch the desktop UI.

Production client endpoints require HTTPS; plain HTTP is allowed only for loopback development URLs.

Build/publish on Windows:

```powershell
./scripts/publish-employee-client.ps1 -Runtime win-x64
```

Install the connectivity service from an elevated PowerShell session:

```powershell
./scripts/install-employee-agent.ps1 `
  -AgentDirectory ./artifacts/employee-client/agent-win-x64 `
  -ServerBaseUrl https://task-monitoring.example.com/
```

See [`docs/employee-desktop.md`](docs/employee-desktop.md) for architecture, privacy, configuration and deployment details.

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

Windows employee client validation:

```powershell
dotnet restore EmployeeClient.slnx
dotnet build EmployeeClient.slnx --configuration Release --no-restore
dotnet test tests/EmployeeClient.Tests/EmployeeClient.Tests.csproj --configuration Release --no-build --no-restore
```

The PostgreSQL integration test expects `TEST_POSTGRES_CONNECTION` to point to an isolated test database. GitHub Actions supplies one automatically.

## Security and monitoring principles

- No plain-text passwords or committed production secrets.
- Server-side authentication, authorization and validation are mandatory.
- Browser/client permission presentation never replaces backend policies.
- Refresh tokens are random opaque values and only their SHA-256 hashes are persisted server-side.
- Admin Web does not persist its current session in `localStorage`.
- Employee Desktop persists only the rotating refresh token and protects it with Windows DPAPI current-user scope; access tokens remain in memory.
- The Windows Agent has no employee credentials and performs backend health/connectivity checks only.
- Desktop self-service employee identity comes from the authenticated JWT `sub`; callers cannot select another employee ID for self-scoped data.
- Deactivating an employee disables the linked account and revokes active refresh tokens.
- Organization-wide attendance, project/task, survey, reporting, role, access-assignment and audit access require explicit permissions.
- Important authentication and business mutations are audit logged.
- Hidden spyware behavior, keylogging, password capture, covert camera/microphone activation and unrelated private-file collection are explicitly out of scope.
- The current desktop heartbeat does not collect screenshots, clipboard, browser history, application-window contents, microphone/camera data, or unrelated private files.
- Any future screenshot, app-usage, URL or location telemetry must have a legitimate business need, clear employee disclosure, explicit authorization boundaries, auditability and retention controls before implementation.

## Development workflow

Large features use focused branches and meaningful commits. A feature is complete only when its applicable API/data/UI layers, validation, permissions, error handling, tests/CI gates and documentation work together.

See [`docs/architecture.md`](docs/architecture.md), [`docs/admin-web.md`](docs/admin-web.md), and [`docs/employee-desktop.md`](docs/employee-desktop.md) for architecture and client details.
