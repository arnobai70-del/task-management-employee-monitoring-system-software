# Task Management & Employee Monitoring System

A centralized, internet-required task management, attendance, survey operations, and transparent employee work-monitoring platform for development teams and survey/field teams.

## Status

Development has started. The repository now contains the backend foundation: ASP.NET Core API structure, PostgreSQL/EF Core data model, JWT authentication with rotating refresh tokens, database-backed roles/permissions, security audit logging, API rate limiting, health/OpenAPI endpoints, automated tests, and CI.

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
- Tests: xUnit v3
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

After the bootstrap administrator is created, remove those bootstrap credentials from the environment.

## Start PostgreSQL

```bash
cp .env.example .env
# Edit .env first and replace placeholder passwords/keys.
docker compose up -d postgres
```

## Run the API

Export the required ASP.NET Core environment variables from your secret store or shell, then:

```bash
dotnet restore TaskMonitoring.slnx
dotnet run --project src/Backend/TaskMonitoring.Api/TaskMonitoring.Api.csproj
```

For local development only, set `Database__AutoMigrate=true` after migrations exist. Production should apply migrations deliberately during deployment.

Health endpoint: `/health`

OpenAPI document: `/openapi/v1.json`

## Tests

```bash
dotnet test TaskMonitoring.slnx
```

CI runs restore, Release build, and backend tests on pull requests and on pushes to `main`.

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
