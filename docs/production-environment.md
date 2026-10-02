# Production Environment Runbook

This runbook defines the supported server-side production deployment path for TaskMonitoring. It complements `windows-production-deployment.md`, which covers employee Windows machines.

## Production topology

The production Compose stack contains four roles:

- **Caddy edge / Admin Web** — the only service that publishes host ports. It terminates HTTPS, serves the React Admin Web, proxies `/api`, `/hubs`, `/health` and `/openapi` to the API, and serves the read-only Windows update directory on a separate site address.
- **ASP.NET Core API** — private Docker-network service on port 8080. It is not published directly to the host.
- **PostgreSQL 17** — private Docker-network service with a named data volume. Port 5432 is not published to the host.
- **Migrator** — one-shot API image invocation using `--migrate-only`. It applies committed EF Core migrations and foundation seeding, then exits. Runtime API containers keep `Database__AutoMigrate=false`.

The `backend` Docker network is internal. The edge container is attached to both the public edge network and the private backend network.

## Host prerequisites

Use a maintained Linux server with:

- Docker Engine and the Docker Compose v2 plugin;
- `bash`, `curl`, `openssl`, `sha256sum` and `python3`;
- persistent storage for Docker volumes, update packages and database backups;
- DNS A/AAAA records for the application and update hosts;
- inbound TCP 80/443 allowed to the edge host;
- no public firewall rule for PostgreSQL 5432.

For Caddy automatic HTTPS, both configured DNS names must resolve to the edge host and ports 80/443 must be reachable during certificate issuance/renewal.

## Environment file

Copy the example and edit real values:

```bash
cp deploy/production/production.env.example deploy/production/production.env
```

Important fields:

- `APP_SITE_ADDRESS` — Caddy site address for Admin/API, for example `task.example.com`;
- `UPDATE_SITE_ADDRESS` — Caddy site address for Windows update files, for example `updates.example.com`;
- `APP_PUBLIC_URL` — public origin including `https://`;
- `UPDATE_PUBLIC_URL` — public update origin including `https://`;
- `ACME_EMAIL` — operations contact for TLS automation;
- `POSTGRES_DB` / `POSTGRES_USER` — PostgreSQL identity;
- secret-file paths — local paths under `deploy/production/secrets/` by default;
- `UPDATE_HOST_ROOT` — host directory bind-mounted read-only into the edge container;
- `BACKUP_ROOT` — host directory for custom-format PostgreSQL backups.

`production.env`, secret files, update content and backups are gitignored.

## Initialize secret files

Run once on a trusted host after editing the environment file:

```bash
bash scripts/production/init-production-secrets.sh deploy/production/production.env
```

The script creates missing values only; it does not overwrite existing secret files. It creates:

- PostgreSQL password;
- API PostgreSQL connection string pointing to the private `postgres` service;
- JWT signing key;
- centralized employee-agent update enrollment key.

The API receives the connection string and secret keys through Docker secrets mounted under `/run/secrets`. `TASKMONITORING_KEY_PER_FILE_DIRECTORY` enables ASP.NET Core's key-per-file configuration provider, so these secrets do not need to be written into Compose environment values.

After initialization, copy the secret values into the organization's approved secret manager. Losing the PostgreSQL password complicates database administration; losing/replacing the JWT signing key invalidates existing signed access tokens.

## First deployment and normal deployment

Use the guarded deployment entry point:

```bash
bash scripts/production/deploy.sh deploy/production/production.env
```

The script:

1. validates required configuration and secret files;
2. validates the Compose model;
3. builds the API and web images;
4. starts PostgreSQL and waits for readiness;
5. creates a custom-format pre-deployment PostgreSQL backup with SHA-256 sidecar;
6. runs the one-shot `--migrate-only` container;
7. starts/replaces API and edge containers;
8. requires `/health/live` and `/health/ready` to succeed before declaring the rollout healthy.

A deployment is not successful merely because containers were created. Readiness is part of the deployment contract.

### First production SuperAdmin

A brand-new production database contains roles and permissions but intentionally does **not** contain a hard-coded administrator account. Immediately after the first successful deployment, create the first organization-owned SuperAdmin with the one-shot bootstrap command:

```bash
bash scripts/production/bootstrap-admin.sh \
  --env deploy/production/production.env \
  --email admin@your-company.example
```

The command prompts for the password twice without echoing it. The email/password are written only to owner-readable temporary files and mounted into a one-shot migrator container as Docker secrets named `BootstrapAdmin__Email` and `BootstrapAdmin__Password`. The API entrypoint copies those optional bootstrap values only for that one container. The temporary files are deleted when the command exits; normal API and migrator containers do not mount or retain bootstrap credentials.

The bootstrap command fails unless it can verify at least one active `SuperAdmin` after seeding. Sign in through the production Admin Web immediately afterward and store the administrator credential according to organization policy. Do not add bootstrap credentials to `production.env`, source control, shell history, Compose environment values, tickets, or logs.

Re-running the bootstrap command with an email that already exists does not reset that user's password; normal administrator account-management/password-reset workflows remain authoritative after the initial account exists.

## Database migration policy

Production API containers always use:

```text
Database__AutoMigrate=false
```

Committed EF migrations are applied only by the explicit migrator job in the deployment script. This makes the schema change visible in deployment logs and ensures the pre-deployment backup is created first.

If the migrator fails, do not advance the rollout until the migration problem is understood. Do not manually edit the EF migrations history table.

## Database backup

Create an on-demand backup:

```bash
bash scripts/production/backup-postgres.sh deploy/production/production.env manual
```

The result is a PostgreSQL custom-format archive and a SHA-256 sidecar under `BACKUP_ROOT`. The script validates that `pg_restore --list` can read the archive before finalizing it.

Copy backups to independent/off-host storage according to the organization's retention policy. A backup that exists only on the same disk as PostgreSQL is not sufficient disaster recovery.

## Database restore

Restore is intentionally confirmation-gated:

```bash
bash scripts/production/restore-postgres.sh \
  --env deploy/production/production.env \
  --backup /path/to/taskmonitoring-YYYYMMDDTHHMMSSZ-manual.dump \
  --confirm-restore
```

The restore script:

- validates the archive checksum when the sidecar exists;
- verifies the archive can be listed;
- stops the API if it is running;
- restores with `--clean --if-exists --no-owner --no-acl --exit-on-error`;
- restarts the API when it had been running.

After any real restore, verify `/health/ready`, sign in through the Admin Web and confirm a known recent business record before reopening normal operations.

## Windows update host

The edge container serves `UPDATE_HOST_ROOT` read-only on `UPDATE_SITE_ADDRESS`. Signed bundles are not copied there manually. Use the atomic publisher:

```bash
bash scripts/production/publish-windows-release.sh \
  --env deploy/production/production.env \
  --bundle /secure/path/TaskMonitoring.EmployeeRelease-X.Y.Z-win-x64
```

The publisher validates manifest schema, channel/version, package file name, size, SHA-256, and that the archived publisher certificate bytes hash to the pinned publisher-certificate SHA-256. It writes versioned archives immutably: retrying the exact same bundle is allowed, but an existing version can never be replaced with different manifest/package/certificate bytes. Build a new semantic version for changed bytes.

The active channel pointer is under `UPDATE_HOST_ROOT/stable/` or `beta/`; immutable rollback archives are retained under `UPDATE_HOST_ROOT/releases/<version>/`.

## Server acceptance

After a signed Windows release is published, run:

```bash
bash scripts/production/acceptance-server.sh \
  --env deploy/production/production.env \
  --channel stable
```

Acceptance requires:

- API liveness succeeds;
- PostgreSQL-backed readiness succeeds;
- Admin Web root returns HTML;
- channel `release.json` is downloadable;
- the referenced runtime package is downloadable;
- downloaded package size and SHA-256 match the manifest.

The final real-production launch gate additionally verifies the signed-bundle and archived publisher certificate bytes against the independently recorded publisher fingerprint.

## Logs and diagnostics

Use Compose logs for server diagnostics:

```bash
docker compose \
  --env-file deploy/production/production.env \
  -f deploy/production/docker-compose.yml \
  logs --tail=200 api web postgres
```

Do not paste secret-file contents into tickets or logs. Database/JWT/bootstrap secret values should not appear in normal Compose configuration output.

## Security boundaries

- PostgreSQL is private to the Docker backend network.
- The API is private to the Docker backend network.
- Only the edge service publishes host ports.
- Production secret values are mounted from local secret files.
- Bootstrap administrator credentials exist only in one-shot temporary Docker secrets and are removed after bootstrap.
- Employee JWT/refresh credentials are not part of server deployment files.
- The update directory is mounted read-only into the edge container.
- Windows packages still require Authenticode verification/publisher pinning on employee machines; HTTPS and SHA-256 are additional controls, not replacements for code signing.

## Recovery order

For a server disaster:

1. provision a clean host and restore the same production environment/secret values from the secret manager;
2. start PostgreSQL;
3. restore the selected verified database archive;
4. run the explicit migrator for the deployed application version;
5. start API/edge services;
6. require `/health/live` and `/health/ready` success;
7. run server acceptance before returning users to service.

Do not run the bootstrap-admin command during disaster recovery when the restored database already contains administrator accounts.
