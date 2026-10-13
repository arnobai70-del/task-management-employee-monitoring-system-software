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

## After fix (branch `fix/phase2-auth-realtime-export-hardening`)
Recorded in that PR (`docs/audit/evidence/2026-10-13/*-after-fix.txt`) so that this matrix only references committed evidence.

## Not yet run (planned)
| Area | Reason | Phase |
| --- | --- | --- |
| Browser E2E / RBAC / visual regression | No suite exists yet (G-17) | 5, 10 |
| Employee A/B, Manager A/B authorization matrix | Not written yet (G-10) | 2 |
| Attendance 20:30→09:00/09:30/10:00, DST, retry/idempotency | Not written yet (G-06) | 3 |
| Signed Windows release | BLOCKED — no signing certificate | 9 |
| RDP two-user isolation | BLOCKED — no Windows Server RDP host | 7 |
| Production smoke / restore rehearsal on real host | BLOCKED — no production access | 9 |
