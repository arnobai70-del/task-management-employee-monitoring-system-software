# Docker-free Windows Admin mode

TaskMonitoring does **not** require Docker on employee PCs or on administrator PCs that only open the central Admin Web in a browser. Docker is only one way to host PostgreSQL for local/server development.

This repository now supports a Docker-free Windows launcher that uses PostgreSQL 17 installed as a normal Windows service.

## Which launcher to use

- `Start-Admin.cmd` — automatic mode. It prefers an installed native PostgreSQL 17 instance and otherwise falls back to Docker Desktop when Docker is available.
- `Start-Admin-Native.cmd` — forces Docker-free PostgreSQL mode. Docker is never started or required.
- `Start-Admin-Docker.cmd` — forces the previous Docker PostgreSQL path.
- `Start-Admin-LAN-Test.cmd` — LAN development mode. Pass `-DatabaseMode Native` when the host PC must stay Docker-free.

The API and Admin Web behavior is the same in both database modes. Only the local PostgreSQL runtime changes.

## Docker-free prerequisites on the machine hosting the local server

Install:

1. PostgreSQL 17 for Windows, including the command-line tools (`psql.exe` and `pg_isready.exe`). Keep the PostgreSQL Windows service enabled.
2. .NET 10 SDK.
3. Node.js 24 or newer.
4. Git if the repository is being updated through Git.

Employee PCs do not need these development prerequisites. They only need the Employee Windows application and network access to the organization server.

## First Docker-free run

1. Copy `.env.example` to `.env` if the launcher has not already created it.
2. Replace every `replace-with-...` value and set `POSTGRES_DB`, `POSTGRES_USER`, and `POSTGRES_PASSWORD` for the local application database.
3. Set the bootstrap administrator email and a password of at least 12 characters for the initial setup.
4. Double-click `Start-Admin-Native.cmd`.

The launcher builds the API connection string in process from those `POSTGRES_*` values and the selected local port, so `-PostgresPort 5433` (or another explicit native port) is applied consistently to both database setup and API startup. The generated process value is not written back to `.env`. The standalone `ConnectionStrings__DefaultConnection` entry remains useful when running the API manually outside the launcher.

If the application role/database do not exist yet, the launcher asks once for the local PostgreSQL administrator password (normally the password chosen for the `postgres` account during PostgreSQL installation). That password is kept only in the current process long enough to create/update the application role and database; TaskMonitoring does not write the PostgreSQL administrator password to `.env`, source files, logs, or the registry.

After the database is initialized, normal launches use the application database credentials already stored in the uncommitted `.env` file and do not need the PostgreSQL administrator password.

The launcher verifies PostgreSQL 17+, starts the PostgreSQL Windows service when possible, verifies the configured application database connection, starts the API, starts the Vite Admin Web, and opens `http://127.0.0.1:5173` unless `-NoBrowser` is supplied.

## Automatic mode

`Start-Admin.cmd` can be used on machines with different setups:

```powershell
Start-Admin.cmd
```

Selection order:

1. Native PostgreSQL, when PostgreSQL command-line tools are installed.
2. Docker Desktop, when native PostgreSQL is not installed and Docker is available.
3. A clear error when neither database runtime is installed.

Use the explicit launchers when you never want automatic selection.

## LAN testing without Docker

From an elevated terminal when Windows Firewall needs to be configured:

```powershell
Start-Admin-LAN-Test.cmd -DatabaseMode Native
```

LAN test mode is intentionally HTTP development traffic on the private LAN. It must not be used as production hosting.

## Database reset

`-ResetLocalDatabase` is destructive in both modes. In native mode it drops and recreates the configured application database; in Docker mode it removes the Docker PostgreSQL volume.

Do not use this switch on a database containing data you need.

## Central server / domain deployment

The client architecture does not require Docker. A central Windows deployment can use:

- PostgreSQL 17 running as a Windows service;
- the ASP.NET Core API hosted by IIS/Kestrel on the central server;
- the built Admin Web served behind the same HTTPS origin/reverse proxy as `/api` and `/hubs`;
- the organization domain and TLS certificate on the central server/reverse proxy;
- Employee Desktop clients configured with the central HTTPS server URL.

For production, keep `Database__AutoMigrate=false`, apply committed EF migrations deliberately, store database/JWT secrets outside source control, use HTTPS, maintain backups with restore evidence, and use the signed Employee release/update path described in `windows-production-deployment.md`.

The existing Docker production stack remains available and is not removed by this mode. Docker-free Windows hosting and Docker hosting are deployment choices for the central server; neither is a requirement for employee workstations.
