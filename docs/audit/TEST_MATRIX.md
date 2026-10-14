# Test Matrix

Environment codes: **L** = audit Linux container (Ubuntu 24.04, .NET SDK 10.0.401 container, PostgreSQL 16.15, Node 22.22) · **CI** = GitHub Actions on the named commit.
All timestamps UTC.

## Baseline (`c38dca3`)

| # | Test / command | Input | Env | Result | Timestamp | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| T-01 | `dotnet tool restore && dotnet restore TaskMonitoring.slnx` | — | L | PASS | 2026-10-13 22:41 | [backend-baseline.txt](evidence/2026-10-13/backend-baseline.txt) |
| T-02 | `dotnet build TaskMonitoring.slnx -c Release` (warnings as errors) | — | L | PASS, 0 warnings | 2026-10-13 22:41 | same |
| T-03 | `dotnet ef migrations has-pending-model-changes` | fresh PostgreSQL DB | L | PASS — no changes | 2026-10-13 22:41 | same |
| T-04 | `dotnet test tests/Backend.Tests` | `TEST_POSTGRES_CONNECTION` → fresh DB | L | PASS 139/139 | 2026-10-13 22:41 | same |
| T-05 | `dotnet list package --vulnerable --include-transitive` | nuget.org | L | PASS — none | 2026-10-13 22:41 | same |
| T-06 | `npm install && npm run build` (src/AdminWeb) | no lockfile | L | PASS (chunk-size warning) | 2026-10-13 22:41 | [frontend-baseline.txt](evidence/2026-10-13/frontend-baseline.txt) |
| T-07 | `npm audit` / `npm audit --omit=dev` | — | L | PASS — 0 vulnerabilities | 2026-10-13 22:41 | same |
| T-08 | gitleaks 8.28.0 history + tree, `--redact` | 76 commits | L | 3 hits, all test fixtures (false positive) | 2026-10-13 22:41 | [secret-scan.txt](evidence/2026-10-13/secret-scan.txt) |
| T-09 | CI backend + EF + tests | push `c38dca3` | CI | PASS | 2026-10-03 16:52 | [run 37138451220](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/37138451220) |
| T-10 | CI production deploy, bootstrap, backup→restore, immutable release publish | push `c38dca3` | CI | PASS | 2026-10-03 16:52 | same run |
| T-11 | CI Windows desktop/service/updater build, unsigned bundle, install→update→rollback→uninstall | push `c38dca3` | CI | PASS | 2026-10-03 16:52 | same run |
| T-12 | Windows Employee Release workflow (dispatch) | `c38dca3` | CI | PASS | 2026-10-03 17:01 | [run 37138971995](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/37138971995) |

## Reproductions of verified bugs (before fix, on baseline code)

| # | Test | Input | Env | Result | Timestamp | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| R-01 (G-21) | `RefreshTokenConcurrencyTests` ×3 | 8 concurrent refreshes, same token, real PostgreSQL | L | FAIL as expected: 5, 7, 7 successes (expected 1) | 2026-10-13 22:44 | [F01 log](evidence/2026-10-13/F01-refresh-race-before-fix.txt) |
| R-02 (G-22) | `scripts/audit/signalr-expiry.mjs` | `Jwt__AccessTokenMinutes=1`, wait 150 s, no refresh | L | Socket still `Connected` after expiry | 2026-10-13 22:47–22:50 | [F02 log](evidence/2026-10-13/F02-signalr-expiry-before-fix.txt) |
| R-03 (G-23) | `Export_neutralizes_spreadsheet_formula_cells_from_untrusted_audit_fields` | User-Agent `=HYPERLINK(...)`, TargetId `@SUM(1+1)`, `+1`, `-2+3` | L | FAIL as expected: raw `=HYPERLINK(` in CSV | 2026-10-13 22:45 | [F03 log](evidence/2026-10-13/F03-csv-injection-before-fix.txt) |

## After fix (#69 / #70)

| # | Test | Input | Env | Result | Timestamp | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| A-01 (G-21/22/23) | Full backend suite | fresh PostgreSQL DB | L | PASS 142/142 (139 + 3 new) | 2026-10-13 22:53 | [backend-after-fix.txt](evidence/2026-10-13/backend-after-fix.txt) |
| A-02 (G-21) | `RefreshTokenConcurrencyTests` ×5 | 8 concurrent refreshes | L | PASS 5/5 (exactly one success each run) | 2026-10-13 22:53 | same |
| A-03 (G-21/G-23) | `AuthServiceTests` (4), `Export_neutralizes_*` | — | L | PASS | 2026-10-13 23:00 | [F01-F03-unit-after-fix.txt](evidence/2026-10-13/F01-F03-unit-after-fix.txt) |
| A-04 (G-22) | `signalr-expiry.mjs` | 1-min tokens, no refresh | L | Server closed socket at expiry (22:55:32) | 2026-10-13 22:54–22:57 | [F02-signalr-expiry-after-fix.txt](evidence/2026-10-13/F02-signalr-expiry-after-fix.txt) |
| A-05 (G-22) | `signalr-reconnect.mjs` | 1-min tokens, refreshing factory | L | Reconnected twice with fresh tokens, final state Connected | 2026-10-13 22:54–22:57 | [F02-signalr-reconnect-after-fix.txt](evidence/2026-10-13/F02-signalr-reconnect-after-fix.txt) |
| A-06 | CI on #69 head `b487248` (backend, admin web, production acceptance, Windows lifecycle) | PR | CI | PASS 4/4 | 2026-10-13 23:04–23:06 | [run 38481932114](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/38481932114) |
| A-07 (G-20) | CI on #70 head `0fca207` with `npm ci` | PR | CI | Admin web, backend, production acceptance PASS; Windows jobs still running when the PR was merged | 2026-10-13 23:05–23:06 | [run 38482050415](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/38482050415) |
| A-08 | CI on main after merges (`d429854`, `a500158`, `10c5102`) — backend, admin web (`npm ci`), production acceptance, Windows lifecycle | push | CI | PASS on all three merge commits, 4/4 jobs each | 2026-10-13 23:06–23:10 | [run 38482189177](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/38482189177), [38482168811](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/38482168811), [38482132213](https://github.com/arnobai70-del/task-management-employee-monitoring-system-software/actions/runs/38482132213) |

| P3-01 (G-06) | `AttendanceShiftEdgeCaseTests` (13): 20:30→09:00/09:30/10:00 work date and end, two breaks = 45 min, 08:00 checkout = 60 min early leave, after-midnight check-in lateness, before-start/after-end rejection, second check-in same night, break/checkout without session, nested break, forgotten checkout, DST spring-forward 7h / fall-back 9h / gap start | Unit (InMemory, injected clock) | L | PASS 13/13; full suite 155/155 | 2026-10-13 23:21 UTC | [P3-attendance-shift-edge-cases.txt](evidence/2026-10-14/P3-attendance-shift-edge-cases.txt) |
| P3-02 | Retry / clock skew | Analysis | — | Repeated check-in returns 409 (`work_session_already_open` / `attendance_already_recorded`), never a second session; all times come from the server clock, so client clock skew cannot change work date or lateness | 2026-10-13 | `AttendanceCoreService.cs:300-361` |

| P4-01 (G-32) | `HttpWebsiteWorkLifecycleTests` (7): assign→start→open→submit→reopen (note required)→approve, double submit/start/approve 409, durable notifications, generic `/api/tasks` status/update guard, cancel compatibility, inactive assignee | HTTP + PostgreSQL | L | PASS 7/7; suite 156/156 | 2026-10-13 | [P4-website-work-lifecycle-http.txt](evidence/2026-10-14/P4-website-work-lifecycle-http.txt) |
| G28-01 | `AttendanceAutoCloseAndCorrectionTests` (15) + `HttpAttendanceCorrectionTests` (2): auto-close timing, breaks, late checkout, next-day check-in, correction math/validation, 401/403/400/200, concurrent close exactly once (control without token: 8 rows) | Unit + HTTP + PostgreSQL | L | PASS 17/17; suite 173/173; migration up/down/up OK | 2026-10-13 | [G28-attendance-auto-close-correction.txt](evidence/2026-10-14/G28-attendance-auto-close-correction.txt) |

## Not yet run (planned)
| Area | Reason | Phase |
| --- | --- | --- |
| Browser E2E / RBAC / visual regression | No suite exists yet (G-17); first layout check `scripts/audit/status-rail-overlap.mjs` (G-33, 5 widths, mocked API); route smoke `scripts/audit/admin-route-smoke.mjs` (G-35, 20 routes); realtime connection count and token-expiry reconnect `scripts/audit/realtime-shared-connection.mjs` (G-34, real API) | 5, 10 |
| Signed Windows release | BLOCKED — no signing certificate | 9 |
| RDP two-user isolation | BLOCKED — no Windows Server RDP host | 7 |
| Production smoke / restore rehearsal on real host | BLOCKED — no production access | 9 |
