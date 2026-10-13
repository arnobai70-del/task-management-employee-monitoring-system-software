# RFC: Transparent Work-Tab Screenshot Capture

| Field | Value |
| --- | --- |
| Status | **DRAFT — implementation blocked until the owner gives written policy approval** (plan §3 rule 8, §7 automation rule) |
| Gap IDs | G-01, G-07, G-09, G-14 |
| Author | Claude Code audit, 2026-10-13 |
| Default | Feature OFF for every team and employee |

## 1. Business justification (to be confirmed by the owner)
Managers want visual evidence that assigned Website Work was performed on the approved site, without monitoring anything else on the employee's computer. The current monitoring contract explicitly excludes screenshots; this RFC defines the narrowest capture that could meet the need.

**Owner decisions required before any code:**
1. Written policy text employees will see and accept (legal/HR review for the jurisdictions where staff work).
2. Which teams/employees are in scope, and who may view images.
3. Retention period (suggested 7–30 days) and export rules.
4. Allowed hostnames list owner (who maintains it).

## 2. Scope
- **In scope:** the visible viewport of one browser tab that the employee explicitly selected as the work tab, whose hostname is on the approved list, during an active work session (checked in, not on break), with consent active.
- **Out of scope / forbidden:** whole desktop, taskbar, other tabs, other windows/apps, other monitors, full-page stitching, DOM/text/form content, cookies, full URLs, keystrokes, audio, camera, Windows Session 0 capture, any fallback to desktop capture.

## 3. Architecture

```
Employee browser (Chrome/Edge, company-managed profile)
  └─ Work Capture extension (MV3, visible toolbar badge "Work tab capture active", Pause/Stop)
       │ 1. employee clicks "Start work capture" on the work tab (user gesture)
       │ 2. extension asks Employee Desktop (native messaging, same user session) for a short-lived capture credential
       │ 3. every N seconds: verify tab still active+visible, hostname approved, not sensitive → capture viewport
       ▼ TLS
API  POST /api/me/screen-capture/sessions/{id}/images   (identity from credential; never from client employeeId)
  ├─ validate session, policy version, hostname, timestamp window, size/MIME/magic bytes, idempotency key
  ├─ encrypt + store object in private object storage (no public ACL), metadata row in PostgreSQL
  └─ audit event
Admin Web  GET /api/screen-capture/images?...  → short-lived, RBAC-checked view URL; every view audited
```

### Browser API choice (must be re-verified against current Chrome/Edge documentation before coding)
| Option | Fit | Notes |
| --- | --- | --- |
| `chrome.tabs.captureVisibleTab` | **Preferred** | Captures the visible area of the active tab only. Needs `activeTab` (user gesture) or host permissions for approved hosts; browser-enforced call rate limit; returns JPEG/PNG data URL. Fails closed when the window is minimized/hidden. |
| `chrome.tabCapture` (+ offscreen document) | Fallback only | Media stream of a tab; heavier, requires user gesture to start, designed for streaming rather than periodic stills. |
| Desktop/Graphics capture APIs | **Rejected** | Cannot guarantee tab-only scope; violates scope rules. |

Enterprise deployment: force-install via Chrome/Edge `ExtensionInstallForcelist` policy for the company profile; extension never hidden, always shows its badge.

### Session binding (RDP / multi-user, G-07)
- Native messaging host runs inside the logged-in user's session, launched by Employee Desktop; it returns a capture credential bound to `{userId, employeeId, deviceId, windowsSessionId, workSessionId}` with ≤10 min lifetime, refreshable only while the desktop session is signed in.
- Two concurrent RDP users on one server get different credentials; server rejects uploads whose credential session does not match.
- Windows service (Session 0) never participates.

## 4. Privacy controls
- Capture stops (and reports `not_capturing` with a reason) when: no selected tab, tab not active/visible, window minimized, workstation locked, RDP disconnected, browser closed, hostname not approved, URL path matches a sensitive pattern, employee pressed Pause/Stop, consent revoked, credential expired, employee on break or checked out.
- Sensitive patterns (block, never redact): login, sign-in, password, OTP/2FA, checkout/payment/billing, account/profile settings, health, banking, webmail. List versioned with the policy.
- Metadata stored: object key, SHA-256, UTC captured/received times, user/session/device/assignment ids, viewport size, approved hostname (no path/query), encoding, byte size, policy version.
- Never stored: full URL, page title, DOM, cookies, typed data.

## 5. API contract (proposed; adapt to existing routes)
| Method | Route | Permission |
| --- | --- | --- |
| GET | `/api/me/screen-capture/policy` | authenticated employee (self) |
| POST | `/api/me/screen-capture/sessions` | `screenshot.capture.self` |
| POST | `/api/me/screen-capture/sessions/{sessionId}/images` | capture credential |
| POST | `/api/me/screen-capture/sessions/{sessionId}/stop` | capture credential |
| GET | `/api/screen-capture/policies` | `screenshot.policy.manage` |
| PUT | `/api/screen-capture/policies/{scopeId}` | `screenshot.policy.manage` |
| GET | `/api/screen-capture/images?employeeId=&from=&to=&page=` | `screenshot.read` (+ manager scope) |
| GET | `/api/screen-capture/images/{id}/view` | `screenshot.read`, audited |
| GET | `/api/screen-capture/audit` | `audit.read` |

New permissions are independent of `monitoring.read` and granted to no role by default.

## 6. Data model (new tables, one migration)
`capture_policies` (scope, enabled=false, allowed_hosts, interval_seconds ∈ {60,120,300}, retention_days, sensitive_patterns_version, policy_text_version) · `capture_consents` (employee, policy version, accepted_at, revoked_at) · `capture_sessions` · `capture_images` (metadata only) · `capture_access_audit` · `capture_retention_state`.

## 7. Storage
Private S3-compatible bucket per environment (or equivalent private backend), SSE AES-256, least-privilege upload/read credentials, no public ACL, lifecycle deletion at retention + secure delete job with audit. Images never in Git, logs, public folders or unrestricted DB blobs.

## 8. Cost/bandwidth guardrails
JPEG/WebP quality cap, max 1920 px longest edge, max 400 KB per image, server rejects larger; start at 300 s interval; measure before allowing 60 s.

## 9. Tests required before enabling (Gate 6)
Cross-employee access denied; disallowed hostname rejected; sensitive path never uploaded; revoked consent stops capture; expired credential rejected; duplicate upload idempotent; retention purge deletes object + metadata and audits; storage failure does not lose session state or fall back to other capture; RDP two-user isolation; feature disabled by default in seed data.

## 10. Alternatives considered
- Desktop screenshots (rejected: violates scope and privacy rules).
- Server-side rendering of the target site (rejected: different content, third-party ToS).
- Manual employee-uploaded proof (cheapest; could be offered first if policy approval is slow).
