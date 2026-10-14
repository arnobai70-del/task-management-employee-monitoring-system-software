# Changelog

All notable changes to this project are recorded here. Dates are UTC.

## [Unreleased]

### Added
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
