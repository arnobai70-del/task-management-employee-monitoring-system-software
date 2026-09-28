# Architecture

## Direction

The product is a client-server system. Employee desktop software requires a valid connection to the central API for normal working functionality. The backend is shared by the future Windows desktop client, Windows background service, and React admin dashboard.

## Repository layout

- `src/Backend/TaskMonitoring.Api` — ASP.NET Core API and business services.
- `tests/Backend.Tests` — backend unit tests.
- `docs` — architecture and operational documentation.
- Future modules will add `src/AdminWeb`, `src/DesktopClient`, and `src/DesktopService` without duplicating backend business rules.

## Backend foundation

The first backend slice is a modular monolith so deployment and transactions stay simple while the domain is still evolving. The current foundation includes:

- PostgreSQL + Entity Framework Core.
- JWT access tokens with short lifetime.
- Opaque rotating refresh tokens; only SHA-256 token hashes are persisted.
- Role and permission entities stored in the database.
- Permission claims and authorization policies.
- Login lockout and API rate limiting.
- Security audit records.
- Central exception handling with safe user-facing errors.
- Health and OpenAPI endpoints.

## Security boundaries

Passwords are hashed with ASP.NET Core `PasswordHasher<TUser>`. JWT signing keys, database passwords, and bootstrap administrator credentials are configuration secrets and must not be committed. Sensitive monitoring features are intentionally outside this foundation and must remain transparent and permission-controlled if later approved.

## Database changes

Schema changes must be added as Entity Framework Core migrations and committed with the code that depends on them. Production startup should normally keep `Database:AutoMigrate=false`; migrations should be applied deliberately during deployment. Local development may enable automatic migration.
