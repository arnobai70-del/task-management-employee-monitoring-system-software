# Feature Matrix (baseline `c38dca3`, 2026-10-13)

Columns: **Expected** (README/docs/plan) · **Implemented** (code found) · **Tested** (automated test that exercises it) · **Released** (evidence of a production deployment). "CI" means covered by the GitHub Actions run on `c38dca3`. Production deployment state is UNKNOWN for every row: no production access was available (G-04).

| Feature | Frontend (Admin Web) | API / DB | Desktop / Agent | Tested | Released |
| --- | --- | --- | --- | --- | --- |
| Login, JWT + rotating refresh, lockout, rate limit | Login page, cookie refresh (`api.ts`) | `AuthController`, `AuthService`, `refresh_tokens` | Desktop in-memory tokens | AuthServiceTests, AuthContractValidationTests, SecurityHardeningTests; race found and fixed (G-21) | UNKNOWN |
| Roles / permissions (34) | Route guards `ProtectedPage` | `PermissionCatalog`, `RolesController` | — | PermissionCatalogTests, EmployeeCoreRoleUpdateTests | UNKNOWN |
| Employees, departments | `/employees`, `/departments` | `EmployeesController`, `DepartmentsController` | — | EmployeeCoreServiceTests, EmployeePasswordResetControllerTests | UNKNOWN |
| Shifts, attendance, breaks | `/shifts`, `/attendance` | `ShiftsController`, `AttendanceController` | Check-in/out/break UI | AttendanceCoreServiceTests (7: grace, duplicate, open break, overnight work date) — 20:30→09:xx, DST, idempotency NOT tested (G-06) | UNKNOWN |
| Projects, tasks | `/projects`, `/tasks` | `ProjectsController`, `TasksController` | My Tasks | ProjectTaskCoreServiceTests (9) | UNKNOWN |
| Website Work assign → work → submit → approve/reopen | `/website-work`, `/follow-ups` | `WebsiteWorkController` (17 endpoints), review/attention/timeline services | Website Work tab | WebsiteWork* tests (≈22) | UNKNOWN |
| Surveys / external survey links | `/surveys` | `SurveyControllers`, `ExternalSurveyController` | Survey workspace | SurveyCoreServiceTests, ExternalSurveyServiceTests | UNKNOWN |
| Access assignments (RDP / IP / websites) | `/access/rdp`, `/access/ip`, `/access/websites` | `AccessAssignmentsController` | Assigned access view | AccessAssignmentServiceTests, regression tests | UNKNOWN |
| Presence / realtime | Presence panel, realtime notices | `RealtimeHub`, `PresenceController` | `EmployeeRealtimeClient` | RealtimeWorkspaceServiceTests; hub expiry fixed + E2E script (G-22) | UNKNOWN |
| Transparent monitoring telemetry (approved apps/domains) | Monitoring management | `MonitoringController`, retention hosted service | `ForegroundActivityCollector` | MonitoringTelemetryServiceTests (8) | UNKNOWN |
| Reports, productivity, SLA analytics, CSV export | `/dashboard`, `/productivity`, `/sla-analytics` | `ReportsController` | — | ReportingDashboardServiceTests, WebsiteWorkProductivityReportServiceTests, FollowUpSlaAnalyticsServiceTests; CSV injection fixed (G-23) | UNKNOWN |
| Operations health + incidents | `/operations` | `OperationsController`, hosted service | Agent health reporter | OperationsHealthServiceTests, OperationsIncidentServiceTests | UNKNOWN |
| Security alerts, audit logs, audit export | `/security-alerts`, `/audit-logs` | `SecurityAlertsController`, `AuditLogsController`, `SecurityObservabilityController` | — | SecurityAlertServiceTests, AuditLogServiceTests, SecurityObservabilityServiceTests | UNKNOWN |
| Admin notifications / follow-up reminders | Notification center | `AdminNotificationController`, reminder hosted service | — | AdminNotificationServiceTests | UNKNOWN |
| Central agent updates, production release control | Agent update + release pages | `AgentUpdatesController`, `ProductionReleasesController` | `EmployeeUpdater` | AgentUpdateServiceTests, ProductionReleaseControlServiceTests; Windows lifecycle in CI | No GitHub releases published; production UNKNOWN |
| Windows installer / signed release | — | update host (Caddy) | `EmployeeSetup`, release scripts | CI unsigned lifecycle acceptance (install/update/rollback/uninstall) | Signed: BLOCKED (G-11) |
| Production compose, backup/restore | — | `deploy/production`, `scripts/production` | — | CI production acceptance (deploy, bootstrap admin, backup→restore probe, immutable release publish) | UNKNOWN |
| Observability overlay (OTLP/Prometheus/Loki/Grafana) | — | Only in open PR #17 | — | PR #17 CI (stale) | Not merged |
| Tab-only screenshot capture | — | Not implemented | Not implemented | — | NEW REQUIREMENT, policy approval pending (G-01/G-14) |
| Windows VPS/RDP multi-session capture isolation | — | — | — | — | BLOCKED (no RDP host) |
| UI redesign / design system | Current CSS (letter badges) | — | — | — | Phase 5 (G-02) |
