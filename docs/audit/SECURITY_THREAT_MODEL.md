# Security Threat Model (baseline `c38dca3`, 2026-10-13)

Method: STRIDE per trust boundary, grounded in code read during the audit. Each threat lists existing controls (with file references), residual risk and gap IDs from [GAP_REGISTER.csv](GAP_REGISTER.csv). This is a living document; it is not a penetration test.

## Assets
1. Employee personal data (name, email, attendance history, activity telemetry).
2. Credentials: password hashes, refresh token hashes, JWT signing key, agent enrollment key, device tokens.
3. Audit log integrity.
4. Signed Windows release packages and `release.json` manifests.
5. Third-party access assignments (RDP endpoints, IPs, websites) — the platform stores assignment metadata, never third-party credentials.
6. Future: work-tab screenshots (highest sensitivity; see RFC).

## Trust boundaries
| # | Boundary | Entry points |
| --- | --- | --- |
| B1 | Internet → Caddy edge | `APP_SITE_ADDRESS` (Admin Web + `/api`, `/hubs`), `UPDATE_SITE_ADDRESS` (static updates) |
| B2 | Caddy → API container | reverse proxy on backend network (`10.77.0.0/24` default) |
| B3 | API → PostgreSQL | private compose network, key-per-file secrets |
| B4 | Employee PC (user session) → API | Desktop JWT, realtime hub, telemetry upload |
| B5 | Employee PC (Windows service, Session 0) → API | agent device token, health only |
| B6 | Update host → Employee updater | signed ZIP + manifest, pinned publisher certificate |
| B7 | CI (GitHub Actions) → artifacts | release workflow, signing secrets |

## STRIDE

| Threat | Boundary | Existing controls (evidence) | Residual / gap |
| --- | --- | --- | --- |
| **S** Credential stuffing / brute force | B1 | Lockout 5 fails / 15 min (`AuthService.cs:32-63`); `auth` rate limit 10/min/IP (`Program.cs:237-246`); PBKDF2 `PasswordHasher` | Rate-limit audit amplification (G-24). Unknown-user path skips hashing → small timing oracle for account existence (RISK, low). |
| **S** Stolen refresh token replay | B1/B4 | Hash-only storage, rotation, family revoke on reuse (`AuthService.cs:105-111`), HttpOnly SameSite=Strict cookie for web (`AuthController.cs:136-147`) | Concurrent redemption minted multiple sessions — **fixed** (G-21). |
| **S** Forged JWT | B1 | HS256 only, issuer/audience/lifetime required, ≥32-byte key enforced at startup (`Program.cs:42-49,172-187`) | Key rotation procedure not documented (OPEN). |
| **S** Spoofed client IP via headers | B2 | Forwarded headers only from configured proxies/CIDR, `ForwardLimit=1`, symmetry (`SecurityRegistration.cs:27-71`, compose `KnownNetworks`) | — |
| **T** Tampered update package | B6 | SHA-256 in manifest, signed Authenticode, pinned certificate fingerprint, immutable version archive (CI acceptance in `ci.yml`) | Real signing never exercised here (G-11). |
| **T** Mass assignment / over-posting | B1 | DTO contracts per endpoint, server-derived identity for `api/me/*` | Formal A/B authorization matrix missing (G-10). |
| **R** Admin action repudiation | B1 | Audit log rows for auth, role, employee, website work actions; hash-verifiable export (`SecurityObservabilityService.cs:107-152`) | Export formula injection — **fixed** (G-23). |
| **I** Horizontal data access (Employee A reads B) | B1/B4 | Fallback policy requires auth; permission policies; self-scoped `api/me/*` controllers | Needs explicit two-employee/two-manager tests (G-10). |
| **I** Realtime leakage after revoke/disable | B1/B4 | Group membership by permission claims at connect (`RealtimeHub.cs:106-148`) | Sockets outlived token expiry — **fixed** with `CloseOnAuthenticationExpiration` (G-22). Max staleness now = access-token lifetime (15 min). |
| **I** XSS token theft | B1 | CSP `script-src 'self'`, no inline script, React escaping, refresh token not JS-readable | Access token in `sessionStorage` (G-05); `connect-src ws: wss:` (G-25). |
| **I** Secrets in repo | B7 | `.gitignore` covers env/secrets/backups; gitleaks clean (fixtures only) | Add gitleaks to CI (OPPORTUNITY). |
| **D** Request floods | B1 | Per-IP auth limiter, device limiter | No global limiter on authenticated endpoints; report range limits exist (366 days export). |
| **D** Long report queries | B3 | `Take(limit)` in reporting, export cap `ExportMaxRecords` | No EXPLAIN baseline (G-12). |
| **E** Privilege escalation via role edit | B1 | `roles.manage` permission, audit trail | A/B tests (G-10). |
| **E** Session-0 service abuse / hidden capture | B5 | Service is health-only by design (`docs/architecture.md`) | Must stay so; RFC forbids Session-0 capture (G-07). |

## Privacy boundaries (monitoring)
- Collected today: approved application names and business domain hostnames, sampled 15–300 s (`docs/monitoring-telemetry.md`), presence heartbeats, attendance events.
- Not collected (policy, verified in docs and code search): keystrokes, clipboard, screenshots, microphone/camera, cookies, full URLs, page content.
- Any change to this list (G-01/G-14) requires written owner policy approval and employee disclosure before implementation.

## Review cadence
Update this file whenever a gap changes status, and at the end of each phase.
