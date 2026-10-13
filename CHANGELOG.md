# Changelog

All notable changes to this project are recorded here. Dates are UTC.

## [Unreleased]

### Added
- `docs/audit/` audit workspace from the owner's master plan: project audit, gap register, feature matrix, threat model, screenshot-capture RFC (draft, awaiting policy approval), test matrix, deployment runbook, release report, committed evidence logs (2026-10-13 baseline `c38dca3`).
- `docs/ui/DESIGN_SYSTEM.md` Phase 5 design draft and route inventory.
- `scripts/audit/signalr-expiry.mjs`, `scripts/audit/signalr-reconnect.mjs` evidence scripts for realtime session expiry.
- Attendance edge-case tests for 20:30→09:00/09:30/10:00 night shifts, breaks, early leave and DST transitions (G-06).

### Fixed
- Refresh tokens are single-use under concurrency: a second simultaneous refresh with the same token gets `409 refresh_conflict` and no tokens (G-21, #69).
- Realtime (SignalR) connections close when the access token expires; clients reconnect with fresh claims (G-22, #69).
- Audit and report CSV exports neutralize spreadsheet formulas in text cells (G-23, #69).
- Admin Web dependencies are locked (`package-lock.json`) and installed with `npm ci` in CI, Docker and the Windows launcher (G-20, #70).
- `docs/admin-web.md` described refresh tokens returned in JSON and stored in `sessionStorage`; the Admin Web actually uses an HttpOnly `SameSite=Strict` refresh cookie.
