# Changelog

All notable changes to this project are recorded here. Dates are UTC.

## [Unreleased]

### Added
- `docs/audit/` audit workspace from the owner's master plan: project audit, gap register, feature matrix, threat model, screenshot-capture RFC (draft, awaiting policy approval), test matrix, deployment runbook, release report, committed evidence logs (2026-10-13 baseline `c38dca3`).
- `docs/ui/DESIGN_SYSTEM.md` Phase 5 design draft and route inventory.
- `scripts/audit/signalr-expiry.mjs`, `scripts/audit/signalr-reconnect.mjs` evidence scripts for realtime session expiry.

### Changed
- Work sessions left open are closed automatically at the scheduled shift end after `Attendance:AutoCloseAfterShiftEndMinutes` (default 240). A late checkout no longer records hours after the shift ended (G-28).
- Attendance managers (`attendance.manage`) can correct a session's start and end with a required reason. The change is audited with before and after values, and the Admin Web Attendance page has a Correct action (G-28).

### Fixed
- `docs/admin-web.md` described refresh tokens returned in JSON and stored in `sessionStorage`; the Admin Web actually uses an HttpOnly `SameSite=Strict` refresh cookie.
