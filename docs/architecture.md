# Architecture

## Direction

The product is a client-server system. Employee desktop software requires a valid connection to the central API for normal working functionality. The backend is shared by the future Windows desktop client, Windows background service, and React admin dashboard.

## Repository layout

- `src/Backend/TaskMonitoring.Api` — ASP.NET Core API and business services.
- `src/Backend/TaskMonitoring.Api/Migrations` — source-controlled Entity Framework Core migrations and model snapshot.
- `tests/Backend.Tests` — backend unit tests plus PostgreSQL migration/seed validation.
- `docs` — architecture and operational documentation.
- Future modules will add `src/AdminWeb`, `src/DesktopClient`, and `src/DesktopService` without duplicating backend business rules.

## Backend foundation

The first backend slice is a modular monolith so deployment and transactions stay simple while the domain is still evolving. The current foundation includes:

- PostgreSQL + Entity Framework Core.
- A source-controlled initial identity/authorization migration.
- JWT access tokens with short lifetime.
- Opaque rotating refresh tokens; only SHA-256 token hashes are persisted.
- Role and permission entities stored in the database.
- Idempotent startup seeding for built-in roles/permissions after the schema exists.
- Permission claims and authorization policies.
- Login lockout and API rate limiting.
- Security audit records.
- Central exception handling with safe user-facing errors.
- Health and OpenAPI endpoints.
- CI checks for Release build warnings/errors, EF model drift, authentication behavior, and real PostgreSQL migration correctness.

## Database startup policy

Production should keep `Database:AutoMigrate=false` and apply committed migrations deliberately during deployment. After the schema is ready, API startup seeds missing built-in identity metadata and optionally creates the one-time bootstrap administrator when bootstrap credentials are supplied. Seeding is idempotent and does not replace existing role or permission assignments.

Local development may set `Database:AutoMigrate=true` so the API applies committed migrations before seeding.

## Security boundaries

Passwords are hashed with ASP.NET Core `PasswordHasher<TUser>`. JWT signing keys, database passwords, and bootstrap administrator credentials are configuration secrets and must not be committed. Sensitive monitoring features are intentionally outside this foundation and must remain transparent and permission-controlled if later approved.

## Database changes

Schema changes must be added as Entity Framework Core migrations and committed with the code that depends on them. The pinned `dotnet-ef` tool and CI `has-pending-model-changes` check prevent entity/model changes from silently drifting away from committed migrations.
