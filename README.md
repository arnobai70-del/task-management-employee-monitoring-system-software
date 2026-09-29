# Task Management & Employee Monitoring System

A centralized, internet-required task management, attendance, survey operations, and transparent employee work-monitoring platform for development teams and survey/field teams.

## Status

Development is active. The repository now contains the secure backend foundation plus the first Employee Core slice.

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
- Health/OpenAPI endpoints, automated tests, EF model-drift checks, and PostgreSQL migration validation in CI.

CI currently validates a warning-free Release build, Entity Framework model/migration consistency, authentication behavior, Employee Core business rules, and the complete migration chain against a fresh PostgreSQL service.

The employee desktop client, background Windows service, admin web dashboard, attendance/work-session, project/task, survey, reporting, notification, realtime-presence, installer, and update modules are not yet implemented.

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

The current administration surface includes:

- `/api/departments` — department listing, creation, and updates.
- `/api/employees` — employee listing/search, provisioning, detail lookup, and updates/deactivation.
- `/api/roles` — role lookup used by employee administration.

Employee and department read/write operations use dedicated permission policies. Creating or updating an employee includes role assignment, so those write operations also require `roles.manage`; this prevents an employee manager from escalating another account to a privileged role without role-management authority.

Employee records are not hard-deleted. Deactivation preserves audit/history references, disables the linked user account, and revokes its active refresh tokens.

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
- Important authentication and administration events are audited.
- Hidden spyware behavior, keylogging, password capture, covert camera/microphone activation, and unrelated private-file collection are explicitly out of scope.
- Any future screenshot, app-usage, URL, or location telemetry must have a legitimate business need, clear disclosure, permissions, and retention controls.

## Development workflow

Large features use focused branches and meaningful commits. A feature is not complete until its backend/data/UI layers (where applicable), validation, permissions, error handling, tests, and documentation work together.

See [`docs/architecture.md`](docs/architecture.md) for the current architecture direction.
