# Task Management & Employee Monitoring System

A centralized, internet-required task management, attendance, reporting, access-assignment, realtime workspace, and transparent employee work-monitoring platform for office teams.

## Status

Development is active. Implemented milestones include:

- Secure ASP.NET Core / .NET 10 backend foundation with PostgreSQL 17 and Entity Framework Core migrations.
- JWT access tokens + rotating opaque refresh tokens, database-backed roles/permissions, audit logging, login lockout and auth rate limiting.
- Employee Core: departments, employee provisioning, supervisors, roles, activation/deactivation, search and paging.
- Attendance Core: shifts, effective-dated assignments, check-in/out, breaks, late/early calculation and overnight-shift handling.
- Project / Task Core: projects, membership, task assignment, priorities, due dates, guarded status transitions, comments and activity history.
- Survey / Field Operations Core: forms, typed questions, assignments, drafts/submission, immutable revisions and supervisor review.
- Reporting / Dashboard Core: workforce, attendance, projects, tasks, employee workload and survey progress.
- RDP/IP/Website access assignments with dedicated permissions, employee self-scoping and audit history.
- Realtime Presence Core and durable employee task notifications over authenticated SignalR.
- Admin Web: responsive React/TypeScript console with permission-aware management, reporting, access workflows and live presence.
- Employee Windows Desktop: sign-in, attendance, tasks, notifications, realtime delivery, assigned access, transparent monitoring disclosure and approved work-activity telemetry.
- Transparent Monitoring Telemetry Core: administrator-approved application rules, opt-in window-title collection, approved business-domain hostname activity, retention controls, audit history and `monitoring.read` / `monitoring.manage` authorization.
- Visible Employee Windows Service: credential-free API/service-health heartbeat only.
- Docker-free Windows Admin development mode using native PostgreSQL 17 as a normal Windows service, while preserving the Docker PostgreSQL path as an explicit fallback.
- Production Windows deployment tooling: self-contained versioned runtime bundle, machine-wide installer/uninstaller, scheduled auto-updater, SHA-256 verification, publisher certificate pinning, service recovery, backup/rollback and deployment-managed server configuration.
- Production Windows signing workflow requiring repository code-signing secrets; unsigned packaging is limited to CI/development validation.
- GitHub Actions quality gates for backend build/tests, EF migration drift, PostgreSQL integration, Admin Web build, Employee Desktop/Service/Updater builds, PowerShell parsing and release-bundle integrity validation.

A production environment still needs organization-specific infrastructure such as HTTPS API/Admin hosting, PostgreSQL backup/restore operations, a stable HTTPS update host, and the organization's code-signing certificate secrets. MSI/MSIX packaging is not currently implemented; the supported production deployment path is the signed self-contained release bundle plus elevated PowerShell installer/updater workflow.

## Architecture

```text
Employee PC
  -> .NET 10 WPF Employee Desktop
     -> authenticated REST self-workspace
     -> attendance/tasks/access/notifications
     -> disclosed presence + approved monitoring telemetry
     -> SignalR employee notification channel
  -> Visible .NET Windows Service
     -> credential-free /health/live reachability heartbeat
  -> SYSTEM Employee Updater
     -> HTTPS release manifest/runtime download
     -> SHA-256 + pinned Authenticode publisher verification
     -> staged activation + rollback

Internet / HTTPS
  -> ASP.NET Core API + SignalR
     -> PostgreSQL 17
  -> React Admin Web
  -> HTTPS Windows update host
```

Normal employee functionality is intentionally server/internet dependent. The product is not designed as an offline-first application.

## Technology baseline

- Backend: ASP.NET Core / .NET 10
- Database: PostgreSQL 17
- ORM: Entity Framework Core
- Authentication: JWT access tokens + rotating opaque refresh tokens
- Authorization: database-backed roles and permissions
- Realtime: ASP.NET Core SignalR
- Admin Web: React 19 + TypeScript + Vite + React Router + SignalR JavaScript client
- Employee Desktop: .NET 10 WPF (`net10.0-windows`) + SignalR .NET client
- Employee Background Service: .NET 10 Worker + Windows Services integration
- Employee Updater: self-contained .NET 10 Windows console executable
- Windows release packaging: self-contained `win-x64` ZIP + PowerShell deployment tooling + Authenticode signing hooks
- API documentation: OpenAPI
- Backend tests: xUnit v3 on Microsoft Testing Platform
- CI/CD: GitHub Actions on Linux and Windows runners

## Repository structure

```text
src/
  Backend/TaskMonitoring.Api/
  AdminWeb/
  EmployeeDesktop/TaskMonitoring.EmployeeDesktop/
  EmployeeService/TaskMonitoring.EmployeeService/
  EmployeeUpdater/TaskMonitoring.EmployeeUpdater/
scripts/
  windows/
    start-admin-local.ps1
    publish-employee-windows.ps1
    install-employee-service.ps1
    uninstall-employee-service.ps1
    deployment-common.ps1
    build-employee-release.ps1
    install-employee-windows.ps1
    uninstall-employee-windows.ps1
    test-employee-release.ps1
tests/
  Backend.Tests/
docs/
  architecture.md
  admin-web.md
  monitoring-telemetry.md
  windows-docker-free-admin.md
  windows-production-deployment.md
.github/workflows/
  ci.yml
  windows-release.yml
```

Backend business rules are authoritative and must not be duplicated or weakened by clients.

## Main API areas

- `/api/auth` — login, refresh and logout.
- `/api/departments`, `/api/employees`, `/api/roles` — organization and employee administration.
- `/api/shifts`, `/api/attendance` — shift and attendance workflows.
- `/api/projects`, `/api/tasks` — project/task operations.
- `/api/me/tasks` — authenticated employee's assigned tasks; employee identity is resolved from JWT server-side.
- `/api/me/access` — authenticated employee's RDP/IP/Website assignments.
- `/api/me/presence/heartbeat` — authenticated employee Desktop presence heartbeat.
- `/api/me/notifications` — employee's durable task notification inbox/read state.
- `/api/me/monitoring/*` — self-scoped monitoring policy and approved app/domain activity ingestion.
- `/api/presence` — permission-gated organization presence roster.
- `/api/monitoring/*` — permission-gated monitoring policy, approved rules and bounded activity views.
- `/api/access-assignments/*` — admin RDP/IP/Website assignment workflows.
- `/api/surveys`, `/api/survey-assignments`, `/api/survey-submissions` — survey workflows.
- `/api/reports/*` — read-only dashboard/reporting endpoints.
- `/hubs/realtime` — authenticated SignalR hub.
- `/health/live` — API process liveness, independent of PostgreSQL.
- `/health/ready` — deployment/load-balancer readiness including PostgreSQL connectivity.
- `/health` — compatibility alias for readiness.
- `/openapi/v1.json` — OpenAPI document.

Permissions are enforced server-side. Organization-wide permission families include employees, departments, roles, shifts, attendance, projects, tasks, surveys, access assignments, reports, presence, monitoring and audit permissions. Employee `/api/me/*` operations derive employee identity from the authenticated account rather than trusting a client-supplied employee ID.

## Admin Web

The browser console lives in `src/AdminWeb` and provides authenticated, permission-aware organization administration. Backend authorization remains authoritative for every request.

Run locally:

```bash
cd src/AdminWeb
npm install
npm run dev
```

Vite listens on port `5173` and proxies `/api` and `/hubs` to `http://localhost:5080` by default. Override the development API target when needed:

```bash
VITE_DEV_API_TARGET=https://localhost:7001 npm run dev
```

Production build:

```bash
cd src/AdminWeb
npm install --no-audit --no-fund
npm run build
```

Output is written to `src/AdminWeb/dist`. See [`docs/admin-web.md`](docs/admin-web.md).

## Employee Windows Desktop, Service and monitoring

The WPF employee client lives in `src/EmployeeDesktop/TaskMonitoring.EmployeeDesktop` and provides employee sign-in, attendance actions, assigned tasks, durable/realtime notifications, presence, assigned access and visible monitoring disclosure.

Desktop access/refresh tokens remain in process memory. They are not written to plaintext files or registry values. Restarting the Desktop requires sign-in again; normal logout revokes the active refresh token.

Transparent monitoring is intentionally constrained:

- only administrator-approved processes are persisted;
- an unapproved foreground process does not cause its window title to be read;
- window titles are captured only for rules with explicit title collection enabled;
- business website telemetry is hostname-only and business-scoped;
- no browser-history scraping or URL path/query capture;
- no keylogging, passwords, screenshots/screen recording, page/form content, microphone, camera or unrelated private-file collection;
- telemetry runs only while the employee is signed into the transparent Desktop application;
- server retention policy and permission gates control storage/viewing.

See [`docs/monitoring-telemetry.md`](docs/monitoring-telemetry.md).

The Windows background worker lives in `src/EmployeeService/TaskMonitoring.EmployeeService`. It contains no employee credentials and performs only a visible machine/service health heartbeat against `/health/live`. Production configuration requires HTTPS unless the explicit development override is enabled.

## Production Windows installer and auto update

The recommended production path is the self-contained release bundle generated by:

```powershell
./scripts/windows/build-employee-release.ps1 `
  -Version "1.0.0" `
  -Channel stable `
  -PackageBaseUrl "https://updates.example.com/taskmonitoring/stable" `
  -PfxPath "C:\secure\company-code-signing.pfx" `
  -PfxPassword $env:TASKMONITORING_PFX_PASSWORD
```

Production packaging requires a code-signing PFX. CI may use `-AllowUnsignedDevelopmentBuild` only to test package mechanics.

First installation from an elevated PowerShell session:

```powershell
./install-employee-windows.ps1 `
  -ServerUrl "https://task-api.example.com" `
  -UpdateManifestUrl "https://updates.example.com/taskmonitoring/stable/release.json" `
  -PublisherCertificateSha256 "<independently-verified-64-hex-certificate-sha256>"
```

The production installer verifies package metadata/hash, runtime version metadata and the pinned Authenticode publisher before activation. It configures a locked organization server URL, Windows Service recovery, Start Menu/Desktop shortcuts, Add/Remove Programs metadata, a SYSTEM update task, and rollback backups.

The scheduled updater checks every four hours by default. It verifies channel/version/package size/SHA-256, enforces HTTPS, verifies Desktop/Service executable version metadata and the pinned publisher, defers while Employee Desktop is open, and restores the previous files if service activation fails. Updater logs are written under `%ProgramData%\TaskMonitoring\logs`.

The production release workflow is `.github/workflows/windows-release.yml` and requires:

- `WINDOWS_SIGNING_PFX_BASE64`
- `WINDOWS_SIGNING_PFX_PASSWORD`

The workflow creates a signed release artifact; publishing `release.json` and the runtime ZIP to the organization's stable HTTPS update host remains an environment-specific deployment action.

See [`docs/windows-production-deployment.md`](docs/windows-production-deployment.md) for the full trust model, release order, certificate rotation, rollback and uninstall procedures.

Legacy service-only publish/install scripts remain available for development/compatibility, but they are not the recommended full production deployment path.

## Backend local prerequisites

For Windows local development/server testing:

- .NET 10 SDK
- Node.js 24 or newer
- PostgreSQL 17 installed as a Windows service **or** Docker Desktop
- Git

Docker is not mandatory. Employee PCs and administrator PCs that only connect to an already-hosted central server do not need Docker, PostgreSQL, .NET SDK, or Node.js.

Copy `.env.example` to `.env` for local configuration and replace all placeholder secrets. Never commit `.env`.

### Windows launchers (Docker-free supported)

- `Start-Admin.cmd` — automatic database mode; prefers native PostgreSQL when installed and otherwise uses Docker when available.
- `Start-Admin-Native.cmd` — forces Docker-free native PostgreSQL 17 mode.
- `Start-Admin-Docker.cmd` — forces the previous Docker PostgreSQL mode.
- `Start-Admin-LAN-Test.cmd -DatabaseMode Native` — Docker-free private-LAN development test mode.

On the first native run, if the application database/role do not exist, the launcher securely prompts for the local PostgreSQL administrator password and provisions the configured application role/database. The administrator password is not written to TaskMonitoring configuration or logs.

See [`docs/windows-docker-free-admin.md`](docs/windows-docker-free-admin.md) for the full Docker-free setup and troubleshooting path.

Required API configuration:

- `ConnectionStrings__DefaultConnection`
- `Jwt__SigningKey` — at least 32 bytes of random secret material

Optional presence configuration:

- `Presence__OnlineThresholdSeconds` — 30 to 600 seconds; defaults to 90.

Optional one-time bootstrap administrator configuration:

- `BootstrapAdmin__Email`
- `BootstrapAdmin__Password` — at least 12 characters

Remove bootstrap credentials after the initial administrator exists.

## PostgreSQL and database migrations

Docker path:

```bash
cp .env.example .env
# Replace placeholder passwords/keys.
docker compose up -d postgres

dotnet tool restore
dotnet ef database update \
  --project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj \
  --startup-project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj
```

On Windows, `Start-Admin-Native.cmd` performs the local native PostgreSQL readiness/provisioning checks and the Development API applies committed migrations when `Database__AutoMigrate=true` in `.env`.

Production should apply committed migrations deliberately and keep `Database__AutoMigrate=false`. CI rejects EF model changes without a committed migration and validates the full migration chain against PostgreSQL.

## Run the API

```bash
dotnet restore TaskMonitoring.slnx
dotnet run --project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj
```

## Validation

Backend:

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

Admin Web:

```bash
cd src/AdminWeb
npm install --no-audit --no-fund
npm run build
```

Windows components and unsigned CI/development bundle validation:

```powershell
dotnet build src/EmployeeDesktop/TaskMonitoring.EmployeeDesktop/TaskMonitoring.EmployeeDesktop.csproj --configuration Release
dotnet build src/EmployeeService/TaskMonitoring.EmployeeService/TaskMonitoring.EmployeeService.csproj --configuration Release
dotnet build src/EmployeeUpdater/TaskMonitoring.EmployeeUpdater/TaskMonitoring.EmployeeUpdater.csproj --configuration Release
./scripts/windows/build-employee-release.ps1 -Version "0.0.0" -AllowUnsignedDevelopmentBuild
./scripts/windows/test-employee-release.ps1 `
  -ReleaseDirectory "artifacts\employee-release\TaskMonitoring.EmployeeRelease-0.0.0-win-x64" `
  -AllowUnsignedDevelopmentBuild
```

The PostgreSQL integration test expects `TEST_POSTGRES_CONNECTION` to point to an isolated test database. GitHub Actions supplies one automatically.

## Security and monitoring principles

- No plaintext passwords or committed production secrets.
- Server-side authentication, authorization and validation are mandatory.
- Refresh tokens are random opaque values; only SHA-256 hashes are stored server-side.
- Employee self-workspace endpoints derive identity from the authenticated account.
- Employee Desktop authentication tokens remain process-memory-only.
- SignalR employee groups are server-resolved from authenticated identity.
- Deactivating an employee disables the linked account and revokes active refresh tokens.
- Important authentication, administration and business mutations are audit logged.
- Monitoring is business-scoped, disclosed, permission-gated and retention-controlled.
- Production Windows update packages are SHA-256 checked and executable publisher identity is pinned to an administrator-configured code-signing certificate fingerprint.
- First-install publisher fingerprint verification must use a trusted channel independent of the downloaded release location.
- Hidden spyware behavior, keylogging, password capture, covert screenshots, camera/microphone activation and unrelated private-file collection are explicitly out of scope.

## Development workflow

Large features use focused branches and meaningful commits. A feature is complete only when its applicable API/data/UI layers, validation, permissions, error handling, tests/CI gates and documentation work together.

See [`docs/architecture.md`](docs/architecture.md), [`docs/admin-web.md`](docs/admin-web.md), [`docs/monitoring-telemetry.md`](docs/monitoring-telemetry.md), [`docs/windows-docker-free-admin.md`](docs/windows-docker-free-admin.md), and [`docs/windows-production-deployment.md`](docs/windows-production-deployment.md).
