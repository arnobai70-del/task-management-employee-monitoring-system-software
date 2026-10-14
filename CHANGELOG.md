# Changelog

All notable changes to this project are recorded here. Dates are UTC.

## [Unreleased]

### Added
- Visual regression tests for the Admin Web: the sign-in, dashboard, employees, permission and not-found pages are compared with committed screenshots on a phone and a desktop, in light and dark, using fixed fictional data. They run in CI as the new `admin-web-visual` job (G-13).
- Browser end-to-end tests in `tests/e2e` (Playwright): sign-in and sign-out, an employee without admin permissions is sent to the permission page and refused by the API, a team lead sees only their own team, and an admin creates an employee through the form. They run in CI as the new `admin-web-e2e` job against a fresh database with fictional people (G-17).
- Team scoping for managers (phase 1): without the new `employees.scope.all` permission, employee lists, attendance, presence, monitoring activity, website work and the productivity report show only the user and the people who report to them, directly or indirectly. Existing roles keep seeing everyone after the upgrade (G-27).
- Team scoping phase 2: the same limit now covers the dashboard and the daily attendance, workload and follow-up SLA reports, the attention report and its actions, project tasks, access and survey-link assignments, survey assignments and reviews, Website Work edits and reviews, and live presence and Website Work events. Unassigned work stays visible (G-27).
- Desktop alerts: an admin can turn on **Desktop alerts** in the Admin Web top bar (in the menu on phones) to get new security alerts and operations incidents as desktop notifications on their PC while the console is in the background (G-47).
- Phase 8 large-range results: every ranged or paged endpoint stays bounded with 5000 fictional employees and a year of attendance.
- `scripts/audit/degradation.mjs`, `scripts/audit/long-range.mjs` and `scripts/audit/perf-extend-history.sql` for Phase 8 degradation and large-range checks on fictional data.
- Prometheus alerts for slow API responses and for monitoring data retention that is stale or failing, with rule unit tests in `scripts/audit/prometheus-alerts.test.yml`, and an alarm map in the deployment runbook (G-43).
- `docs/audit/` audit workspace from the owner's master plan: project audit, gap register, feature matrix, threat model, screenshot-capture RFC (draft, awaiting policy approval), test matrix, deployment runbook, release report, committed evidence logs (2026-10-13 baseline `c38dca3`).
- `docs/ui/DESIGN_SYSTEM.md` Phase 5 design draft and route inventory.
- `scripts/audit/signalr-expiry.mjs`, `scripts/audit/signalr-reconnect.mjs` evidence scripts for realtime session expiry.
- Admin Web dark theme with a System / Light / Dark selector in the top bar. The choice is kept in the browser, and System follows the operating system setting (G-37).
- Optional private observability stack (OpenTelemetry collector, Prometheus, Loki, Grafana) bound to localhost, from the earlier observability work brought up to date with main. Telemetry export stays off until `Observability:OtlpEndpoint` is set (G-15).
- `scripts/audit/csp-connect-src.mjs` browser check for the production CSP against a local API.
- `scripts/audit/perf-seed.sql` and `scripts/audit/api-latency.mjs` for repeatable API latency baselines on fictional data.
- Attendance edge-case tests for 20:30→09:00/09:30/10:00 night shifts, breaks, early leave and DST transitions (G-06).

### Changed
- Website assignment audit records now store only the site's host instead of the full URL, so a token in a URL's query string no longer ends up in the audit log. The assignment itself still keeps the full URL (G-39).
- Admin Web background refreshes pause while the tab is hidden and catch up when it is shown again, slow down while the server answers 429/5xx or is unreachable (honouring Retry-After), and a page that failed to load for those reasons retries on its own. A hidden tab used to send 28-40 requests a minute (G-46).
- The Admin Web header and sidebar badge now show whether the console is live, reconnecting or offline. Before, they always said live, even with the network gone (G-45).
- Backend HTTP tests that configure the API through environment variables now run one at a time, which removes an intermittent CI failure (G-44).
- Microsoft ASP.NET Core, EF Core and Extensions packages updated from 10.0.12 to 10.0.13. This clears advisory GHSA-334f-vvp4-7g6p in SignalR.Common, which was failing the Windows client build (G-42).
- `/health/ready` reports unhealthy while any database migration is pending, so a deploy fails instead of serving an out-of-date schema (G-43).
- The production Content-Security-Policy allows browser connections only to the site's own origin (`connect-src 'self'`). The realtime hub keeps working because it is served from the same origin (G-25).
- Admin Web presence shows "On shift", "On break", "Connected, not on shift" and "Offline" instead of "Working"/"Idle", and started website work shows "In progress". The labels now say what is measured, not effort (G-08).
- Removed the unused `WebsiteWorkService.CompleteAsync`, which could mark work done without review; completion always goes through submit and approve (G-31).
- Admin Web loads each page on demand and starts the realtime client only after sign-in. The first download drops from 145.6 kB to 93.8 kB gzip (G-35).
- Each Admin Web tab now uses one realtime (SignalR) connection instead of up to four, and keeps it across page changes (G-34).
- Work sessions left open are closed automatically at the scheduled shift end after `Attendance:AutoCloseAfterShiftEndMinutes` (default 240). A late checkout no longer records hours after the shift ended (G-28).
- Attendance managers (`attendance.manage`) can correct a session's start and end with a required reason. The change is audited with before and after values, and the Admin Web Attendance page has a Correct action (G-28).

### Fixed
- Saving the monitoring policy, adding or editing approved applications and business domains, and recording approved activity from the Employee Desktop no longer fail with a server error; invalid values get a clear 400 instead (G-56).
- Keyboard users of the Admin Web can skip the navigation with a "Skip to main content" link, the phone menu no longer leaves its hidden links in the Tab order (and its button reports open/closed and closes with Escape), and Quick jump keeps focus inside itself and gives it back when closed (G-52, G-53, G-54).
- Failed loads and actions in the Admin Web are now announced by screen readers; success messages are announced politely (G-55).
- Database backups no longer fail on real-size databases: the check that read the archive back died of a broken pipe on anything over a few hundred kilobytes, which also stopped deployments at their pre-deployment backup (G-50).
- Database restore is now all-or-nothing and replaces the whole schema, so rolling back to a backup from before a release that added tables works, and a failed restore leaves the database exactly as it was (G-49).
- A shift that starts or ends at a local time skipped by a daylight-saving change (for example 02:30 on the spring-forward night) now starts or ends at the first minute that exists (03:00) instead of check-in failing with "no shift assigned" that day (G-29).
- A team-limited lead could rename an employee outside their team (the change was saved although the page said "not found") and reset that employee's password. Both now answer "not found" without changing anything (G-48).
- Refresh tokens are single-use under concurrency: a second simultaneous refresh with the same token gets `409 refresh_conflict` and no tokens (G-21, #69).
- Realtime (SignalR) connections close when the access token expires; clients reconnect with fresh claims (G-22, #69).
- Audit and report CSV exports neutralize spreadsheet formulas in text cells (G-23, #69).
- Admin Web text meets WCAG AA contrast on every page in both themes: the "Ctrl K" hint and status badges in panel headings were too faint (G-37).
- IP assignments reject shorthand, whole-number, hex and zero-padded addresses that were silently saved as a different address (for example `192.168.010.025` became `192.168.8.21`). RDP hosts must be a hostname or IP address without scheme, port or path (G-38).
- The security dashboard no longer loads every audit row in the selected window into memory; a 7-day view at 1000 employees answers in about half the time (G-40).
- The database migration test uses its own temporary database, so it can be rerun on the same server (G-26).
- Rate-limited requests (HTTP 429) are audited at most once per source every 10 seconds with a count of the rest, instead of one database insert each; a very long User-Agent no longer stops the rejection being audited (G-24).
- Approving website work now sends the live "Work completed" event, so managers see the toast and the Website Work completions list updates without a reload (G-36).
- Admin Web live status pills (incidents, security, notifications, follow-ups) no longer overlap each other or the page title; they share one wrapping row under the top bar (G-33).
- Admin Web dependencies are locked (`package-lock.json`) and installed with `npm ci` in CI, Docker and the Windows launcher (G-20, #70).
- `docs/admin-web.md` described refresh tokens returned in JSON and stored in `sessionStorage`; the Admin Web actually uses an HttpOnly `SameSite=Strict` refresh cookie.
