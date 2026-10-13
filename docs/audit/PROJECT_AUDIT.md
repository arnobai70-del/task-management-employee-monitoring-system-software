# Project Audit — Task Management & Employee Monitoring System

| Field | Value |
| --- | --- |
| Audit plan | `TaskMonitoring_Full_Audit_Claude_Master_Plan_2026-10-14.txt` (v1.0, supplied by the owner) |
| Audit start | 2026-10-13 (UTC) |
| Baseline commit | `main` @ `c38dca3fee7c0452d1e381edbe9be22f530b44e6` ("fix: allow first Windows install without existing update task (#67)", 2026-10-03) |
| Baseline CI | [CI run #344 — success](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/37138451220), [Windows Employee Release #3 — success](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/37138971995) |
| Auditor | Claude Code (autonomous, GitHub-connected Linux container) |
| Status | Phase 0 and Phase 1 complete (Linux scope). Phase 2 in progress: G-20…G-23 fixed and merged (#69, #70); authorization matrix tests in #72. Phase 3 edge-case tests added; G-28 needs an owner decision. Old PRs #9/#24 closed as superseded. Nothing deployed. |

Status vocabulary used everywhere in `docs/audit/`:

- **VERIFIED BUG** – reproduced with a failing test or script; evidence committed.
- **RISK** – code reading shows a weakness; not exploited/reproduced end to end.
- **OPPORTUNITY** – improvement, nothing is broken.
- **NEW REQUIREMENT** – agreed scope change, not a defect.
- **UNKNOWN** – needs access this audit does not have.
- **BLOCKED** – step cannot run here; the missing access is named.

Nothing in this document claims the system is bug-free or production ready.

---

## 1. Phase 0 — Repository discovery and safety baseline

| Plan step | Result | Evidence |
| --- | --- | --- |
| Verify GitHub access, HEAD SHA, visibility | DONE. Repo attached with push access; default branch `main`, HEAD `c38dca3`, 78 commits, no branch protection on any branch (`protected: false` for all 7 branches). | GitHub API `list_branches`; `git log` |
| Inventory source, config, CI/CD, PRs, issues, branches | DONE. See §3 inventory. 307 tracked files, ~42.7k lines under `src/`. 4 workflows, 3 open PRs, 0 open issues, 7 branches. | §3 |
| Active changes on main; compare 3 open PRs | DONE. All three PRs are `mergeable_state: dirty` (conflict with main). See §4. | §4 |
| Secret scan, dependency/SCA scan, .gitignore | DONE. gitleaks 8.28.0 on full history + tree: 3 hits, all test fixtures (false positive). `dotnet list package --vulnerable --include-transitive`: none. `npm audit`: 0. `.gitignore` excludes `.env`, `production.env`, `secrets/`, `backups/`, `updates/`, `bin/obj`, `node_modules`, `dist`. | [secret-scan.txt](evidence/2026-10-13/secret-scan.txt), [backend-baseline.txt](evidence/2026-10-13/backend-baseline.txt), [frontend-baseline.txt](evidence/2026-10-13/frontend-baseline.txt) |
| Identify DB schema, generated files, signing, update host, production deployment strategy | DONE (repository side). 8 EF Core migrations (2026-09-29 → TransparentMonitoringTelemetry), PostgreSQL 17 target. Production = Docker Compose (`deploy/production/docker-compose.yml`) behind Caddy with automatic TLS, one-shot `--migrate-only` container, `Database__AutoMigrate=false` for the API, key-per-file secrets, separate static update host. Windows releases signed via `scripts/windows/build-employee-release.ps1` with pinned publisher certificate SHA-256. **Live production host, running SHA and signing certificate: UNKNOWN (no access).** | `deploy/production/*`, `scripts/production/*`, `docs/windows-production-deployment.md` |
| Protected feature branch, reproducible environment | PARTIAL. Work is on feature branches + PRs. Branch protection on `main` is **not enabled** (needs repository admin; see G-19). Reproducible env: .NET SDK 10.0.401 (`mcr.microsoft.com/dotnet/sdk:10.0`, matches `global.json`), Node 22 locally / Node 24 in CI. Admin Web has **no committed lockfile** (G-20). | §5 |
| Feature/status matrix + first audit PR | DONE — [FEATURE_MATRIX.md](FEATURE_MATRIX.md), this PR. | |

**GATE 0: PASSED** — baseline SHA, CI state, PR state and deployment strategy are recorded before any code change. No deployment attempted.

## 2. Phase 1 — Reproducible build, tests and error inventory

| Plan step | Result | Evidence |
| --- | --- | --- |
| Compare README/docs with implementation; fix stale claims | DONE for auth docs: `docs/admin-web.md` claimed the refresh token is returned in JSON and kept in `sessionStorage`. The code uses an HttpOnly, Secure, `SameSite=Strict` cookie scoped to `/api/auth/web` (`AuthController.cs:40-85,136-147`, `api.ts:126`). Corrected in this PR (G-05 → mostly already mitigated). Other docs: spot-checked, no further contradictions found yet; full doc sweep remains OPEN. | `docs/admin-web.md` diff |
| Restore/build .NET solution, run backend tests | DONE. Restore, Release build (warnings as errors) and **139/139 tests passed** on baseline. | [backend-baseline.txt](evidence/2026-10-13/backend-baseline.txt) |
| EF migration drift, migrations against isolated PostgreSQL | DONE. `has-pending-model-changes`: no changes. `DatabaseMigrationTests` applied all migrations to a fresh database. Deviation: local PostgreSQL **16.15** was used because Docker Hub rate-limited (`429`) the `postgres:17-alpine` pull; CI uses 17. | same |
| Locked frontend install, TypeScript + Vite build | DONE with caveat: build passes (`tsc` + Vite 8). There is **no `package-lock.json`** and CI runs `npm install`, so builds are not locked (G-20). Single JS chunk 567 KB raw / 144 KB gzip (G-18). | [frontend-baseline.txt](evidence/2026-10-13/frontend-baseline.txt) |
| Build WPF Desktop/Service/Updater on Windows runner | DONE via existing CI evidence, not re-run here (Linux container cannot build WPF). CI job "Employee Windows client, release and lifecycle acceptance" passed on `c38dca3` (run 37138451220). | CI link above |
| PowerShell signing/packaging lifecycle in CI development mode | DONE via existing CI: unsigned dev bundle build, bundle validation, install→update→rollback→uninstall acceptance all run in the same CI job. Real signed build: **BLOCKED — no signing certificate available to this audit.** | `.github/workflows/ci.yml` job `employee-windows` |
| Test failures, analyzers, exception handling, logs | DONE: 0 failures, 0 warnings (TreatWarningsAsErrors). Global exception handler returns ProblemDetails. | |
| Licensing, telemetry/exception leakage, OWASP auth/API | PARTIAL. Auth/API review produced F-01…F-06 below. Licence inventory: OPEN (no SPDX/licence scan tool run yet). | §6 |
| Reproducible issues with file/line and failing tests | DONE for F-01, F-02, F-03 (failing test / script committed before the fix). | §6 |

**GATE 1: PASSED for Linux-runnable scope** — builds reproducible (except the unlocked npm tree, G-20), CI evidence captured, verified bugs separated from risks.

## 3. Inventory (baseline `c38dca3`)

| Area | Path | Notes |
| --- | --- | --- |
| API (modular monolith) | `src/Backend/TaskMonitoring.Api` | 29 controllers, ~177 HTTP endpoints, 36 services, 1 SignalR hub (`/hubs/realtime`), 5 hosted services, 8 migrations |
| Admin Web | `src/AdminWeb` | React 19.3, React Router 7.18, Vite 8.3, TypeScript 7.0, `@microsoft/signalr` 10.0.11; 18 nav routes + login/forbidden/404 |
| Employee Desktop | `src/EmployeeDesktop/TaskMonitoring.EmployeeDesktop` | WPF .NET 10; foreground app/domain activity collector, agent health, realtime client |
| Employee Service | `src/EmployeeService/TaskMonitoring.EmployeeService` | Windows service; health/reachability only (no capture) |
| Employee Updater / Setup | `src/EmployeeUpdater`, `src/EmployeeSetup` | signed manifest updater, standalone installer |
| Tests | `tests/Backend.Tests` | xUnit v3, 139 cases, InMemory + one real-PostgreSQL migration test |
| Production | `deploy/production`, `scripts/production` | Compose + Caddy, secrets init, deploy, backup/restore, release publish, acceptance |
| Windows scripts | `scripts/windows` | build/sign/test release, install/uninstall/rollback, central update |
| CI | `.github/workflows` | `ci.yml` (backend, admin-web, production backup/restore acceptance, Windows lifecycle), `windows-release.yml`, `one-click-setup.yml`, `production-launch-tooling.yml` |
| Branches | 7 | `main`, 3 open-PR branches, `feat/internal-unsigned-windows-release`, `fix/windows-installer-missing-task`, `fix/windows-release-parameter-splatting` (last three already merged via #65–#67; stale) |

Permission catalog (`Security/PermissionCatalog.cs`): 34 permissions incl. `presence.read`, `monitoring.read/manage`, `audit.read/export`, `security.alerts.manage`. Role seeding: 9 roles.

## 4. Open PR reconciliation (G-03)

| PR | Branch head | Diverged | Finding | Recommendation |
| --- | --- | --- | --- | --- |
| [#9](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/pull/9) employee desktop self-service + Windows agent (draft) | `ade0af8` | base `7d9cd54`; main is 68 commits ahead, PR 41 ahead; conflicts | Superseded. PR builds a **WinForms** client (`MainForm.cs`) + `EmployeeAgent` + migration `20260929201008_EmployeeClientPresence`. Main instead shipped the **WPF** desktop + `EmployeeService` (commits `ac41e83`, `3770949`), presence via `RealtimePresenceNotifications` migration, and later revocation fixes (`72bcddb`, `c694a5a`). Merging #9 would add a second desktop client and a conflicting migration. | Close as superseded by `ac41e83`/`3770949` (owner decision). |
| [#24](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/pull/24) website work completion review | `eafc571` | base `b6def04`; main 55 ahead; conflicts | Superseded. Same title merged to main as `6d40aea` (2026-09-30). Main's `WebsiteWorkReviewService.cs` has since evolved (diff vs PR: 278+/383−). Approve/Reopen/Submit endpoints exist in `WebsiteWorkController.cs:99,107,180`. | Close as superseded by `6d40aea`. |
| [#17](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/pull/17) private observability stack | `59203d1` | base `e027467`; main 61 ahead; conflicts | **Not** superseded. Main has no OTLP/Prometheus/Loki/Grafana overlay (`grep OpenTelemetry` → none). Main does have in-app operations/security observability (`OperationsHealthService`, `SecurityObservabilityService`), which the overlay must not duplicate. | Keep open; rebase onto main and re-run CI in a dedicated Phase 8 PR. Not deployed (G-15). |

**Outcome (2026-10-13):** on the owner's decision, #9 and #24 were closed with a comment naming the replacing commits; their branches are kept. #17 remains open for a Phase 8 rebase.

## 5. Environment used for evidence

- Container: Ubuntu 24.04, Docker 29.8, .NET SDK 10.0.401 in `mcr.microsoft.com/dotnet/sdk:10.0`, Node 22.22, PostgreSQL 16.15 (local).
- Blocked hosts: `builds.dotnet.microsoft.com` (egress policy 403), Docker Hub (429 rate limit).
- Not available: Windows runner (covered by GitHub Actions), production hosts, signing certificate, RDP/VPS machines.

## 6. Confirmed findings (this audit)

Severity: P0 = security/data loss/production breaker, P1 = correctness/deployment, P2 = refinement.

### F-01 Refresh token can be redeemed concurrently into many sessions — VERIFIED BUG, P1 (G-21)
- **Where:** `src/Backend/TaskMonitoring.Api/Services/AuthService.cs:88-128` (`RefreshAsync` reads, checks `IsRevoked`, then saves without a concurrency guard).
- **Repro:** `tests/Backend.Tests/RefreshTokenConcurrencyTests.cs` (real PostgreSQL) sends 8 simultaneous refreshes with one token. Before fix: **5, 7, 7 successful rotations** in three runs (expected 1). [log](evidence/2026-10-13/F01-refresh-race-before-fix.txt)
- **Impact:** a stolen refresh token raced against the legitimate client yields independent valid sessions; one-time-use rotation is not enforced.
- **Fix:** optimistic concurrency token on `refresh_tokens.RevokedAtUtc` (no schema change; EF drift check clean); losing request gets `409 refresh_conflict` and no tokens; a token re-presented within 15 s of its own rotation is treated as a client race (409) rather than theft, older replays still revoke the family. Admin Web retries once after 750 ms. **Status: FIXED — merged in [#69](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/pull/69) (merge `a500158`); PR CI green ([run 38481932114](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/38481932114)). Not deployed.**

### F-02 SignalR connections outlive token expiry and account disable — VERIFIED BUG, P1 (G-22)
- **Where:** `Program.cs:303` maps the hub without `CloseOnAuthenticationExpiration`; `Hubs/RealtimeHub.cs:106-148` assigns permission groups (presence, operations, security alerts, task managers) only at connect time.
- **Repro:** `scripts/audit/signalr-expiry.mjs` with `Jwt__AccessTokenMinutes=1`: socket still `Connected` 150 s after the token expired. [log](evidence/2026-10-13/F02-signalr-expiry-before-fix.txt)
- **Impact:** a deactivated user, or one whose permission was removed, keeps receiving presence/security-alert broadcasts until they close the tab.
- **Fix:** `CloseOnAuthenticationExpiration = true`; existing clients already use `withAutomaticReconnect` + token factory and reconnect with fresh claims (verified, see TEST_MATRIX). **Status: FIXED — merged in #69. Not deployed.**

### F-03 CSV/formula injection in audit and report exports — VERIFIED BUG, P1 (G-23)
- **Where:** `Services/SecurityObservabilityService.cs:302-306` (`Csv()` only escapes quotes); `src/AdminWeb/src/ProductivityReports.tsx:141`, `EscalationSlaAnalytics.tsx:92`.
- **Attack path:** any unauthenticated client sets `User-Agent: =HYPERLINK(...)` on a failed login → stored in `audit_logs.UserAgent` (`AuthService.cs:43`) → exported by an auditor → evaluated by Excel/LibreOffice.
- **Repro:** `SecurityObservabilityServiceTests.Export_neutralizes_spreadsheet_formula_cells_from_untrusted_audit_fields` failed before the fix. [log](evidence/2026-10-13/F03-csv-injection-before-fix.txt)
- **Fix:** prefix `'` to text cells starting with `= + - @ \t \r` (server and both client exporters; numeric cells unchanged). **Status: FIXED — merged in #69. Not deployed.**

### F-04 Rate-limit rejections each write an audit row — RISK, P2 (G-24)
- `Program.cs:207-236`: every 429 performs a synchronous DB insert. A flood against `/api/auth/*` turns rate limiting into DB write amplification. Suggest: sample/aggregate (e.g. one row per partition per window). Not changed yet.

### F-05 CSP `connect-src` allows any WebSocket host — RISK, P2 (G-25)
- `deploy/production/Caddyfile:187`: `connect-src 'self' ws: wss:`. `'self'` already covers same-origin `wss:` in current browsers; the bare schemes allow exfiltration to any host if XSS ever lands. Suggest `connect-src 'self'` after verifying SignalR in the target browsers.

### F-06 Stale admin auth documentation — VERIFIED (doc defect), P2 (G-05)
- `docs/admin-web.md:33-49` described JSON refresh tokens in `sessionStorage`. Corrected in this PR. Remaining exposure: the 15-minute **access** token is still in `sessionStorage` (XSS-readable); mitigated by CSP `script-src 'self'`. Kept as RISK.

### F-07 No committed Admin Web lockfile — VERIFIED, P1 (G-20)
- `src/AdminWeb` has no `package-lock.json`; CI uses `npm install`. Exact dependency tree differs per build. **Status: FIXED — merged in [#70](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/pull/70) (`10c5102`).**

### F-08 Migration test is not re-runnable on a used database — VERIFIED, P2 (G-26)
- `tests/Backend.Tests/DatabaseMigrationTests.cs` inserts fixed rows (`migration-test@example.com`, `MIG-001`); a second run on the same database fails. CI is unaffected (fresh service container). Suggest a unique database per run.

### Other observations (not defects)
- Production config is sound on inspected points: `AutoMigrate=false` + explicit migrate container, forwarded headers restricted to the backend CIDR with `ForwardLimit=1`, HSTS, `frame-ancestors 'none'`, JWT HS256 only, 30 s skew, 15 min access / 7 day refresh, lockout 5 attempts / 15 min, auth rate limit 10/min/IP.
- NuGet packages are one patch behind (10.0.12 → 10.0.13); no known vulnerabilities reported. OPPORTUNITY.
- Presence UI maps `InProgress` → "Working" (`PresencePanel.tsx:125`); the label reflects heartbeat + open session, not verified productivity (G-08, design item for Phase 5).

## 7. Phase status overview

| Phase | Status | Next action |
| --- | --- | --- |
| 0 Discovery | DONE | — |
| 1 Build/tests | DONE (Linux scope); Windows via CI; lockfile merged (#70) | licence scan |
| 2 Security & data integrity | IN PROGRESS — F-01/F-02/F-03 fixed and merged (#69); HTTP authorization matrix (G-10) in #72, no defect found | G-27 owner decision, F-04/F-05, backup encryption review |
| 3 Attendance/breaks | TESTED — 13 edge-case tests pass (20:30→09:00/09:30/10:00, breaks, early leave, DST, retry); no core logic defect | G-28 correction/auto-close policy (owner decision), G-29, G-30 |
| 4 Tasks/Website Work | NOT STARTED — PR #24 superseded | SignalR durable fallback tests |
| 5 UI redesign | NOT STARTED — [DESIGN_SYSTEM.md](../ui/DESIGN_SYSTEM.md) draft | route inventory done (§3) |
| 6 Tab screenshot | RFC DRAFT ONLY — **implementation needs owner's written policy approval** | [SCREENSHOT_CAPTURE_RFC.md](SCREENSHOT_CAPTURE_RFC.md) |
| 7 RDP support | BLOCKED — no Windows Server RDP test host | owner to provide a test VPS |
| 8 Performance/observability | NOT STARTED — PR #17 to rebase | |
| 9 Production pipeline | BLOCKED — no production host/secret/deploy access | [DEPLOYMENT_RUNBOOK.md](DEPLOYMENT_RUNBOOK.md) |
| 10 Final E2E | NOT STARTED | |
