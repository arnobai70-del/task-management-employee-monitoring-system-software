# Task Management & Employee Monitoring System

A centralized, internet-required task management, attendance, survey operations, and transparent employee work-monitoring platform for development teams and survey/field teams.

## Status

Development has started. The repository now contains the backend foundation: ASP.NET Core API structure, PostgreSQL/EF Core data model and committed migration, JWT authentication with rotating refresh tokens, database-backed roles/permissions, security audit logging, API rate limiting, health/OpenAPI endpoints, automated tests, and CI.

CI validates a warning-free Release build, Entity Framework model/migration consistency, authentication behavior, and the initial migration against a real PostgreSQL service.

The employee desktop client, background Windows service, admin web dashboard, attendance, project/task, survey, reporting, and realtime modules are not yet implemented.

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
      Migrations/
tests/
  Backend.Tests/
docs/
.github/workflows/
```

Additional clients will be added as their phases begin; backend business rules should not be duplicated in clients.

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

After changing EF entities or mappings, add and commit a migration. CI also rejects model changes that do not have a matching migration.

## Run the API

Export the required ASP.NET Core environment variables from your secret store or shell, ensure the database migration has been applied, then:

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
- Refresh tokens are random opaque values and only their SHA-256 hashes are stored.
- Important authentication events are audited.
- Hidden spyware behavior, keylogging, password capture, covert camera/microphone activation, and unrelated private-file collection are explicitly out of scope.
- Any future screenshot, app-usage, URL, or location telemetry must have a legitimate business need, clear disclosure, permissions, and retention controls.

## Development workflow

Large features use focused branches and meaningful commits. A feature is not complete until its backend/data/UI layers (where applicable), validation, permissions, error handling, tests, and documentation work together.

See [`docs/architecture.md`](docs/architecture.md) for the current architecture direction.
