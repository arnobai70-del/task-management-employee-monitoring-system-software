# Production Security Hardening and Permission Audit

This document records the production security baseline for the TaskMonitoring API, Admin Web, reverse proxy, and production secret handling.

## Authorization is secure by default

The API registers an authorization fallback policy that requires an authenticated user. Controllers or actions that do not declare a more specific permission policy are therefore authenticated by default instead of becoming public by omission.

The intentionally anonymous controller surface is limited to:

- native sign-in and refresh: `POST /api/auth/login`, `POST /api/auth/refresh`;
- Admin Web sign-in and refresh: `POST /api/auth/web/login`, `POST /api/auth/web/refresh`;
- centrally managed agent enrollment/update control: `POST /api/agent-updates/device/register`, `GET /api/agent-updates/device/{deviceId}/plan`, and `POST /api/agent-updates/device/{deviceId}/status`.

The agent endpoints are anonymous only in the ASP.NET JWT sense. Enrollment requires the enrollment secret and plan/status calls require the per-device secret. Dedicated rate limits protect those endpoints.

Liveness/readiness health endpoints are explicitly anonymous for infrastructure probes. OpenAPI is mapped only in the Development environment and is not exposed by the production API.

## JWT and session baseline

Production startup requires non-empty JWT issuer/audience metadata and a signing key of at least 32 bytes. Access tokens are restricted to 1–60 minutes and refresh tokens to 1–30 days. JWT validation requires issuer, audience, expiration, a valid signature, and the HS256 algorithm. Validation keeps a 30-second clock skew.

Native clients keep the existing token response/rotation contract. Refresh tokens are random, stored server-side only as SHA-256 hashes, rotated on refresh, and reuse of a revoked refresh token revokes the user's remaining active refresh-token family.

Admin Web uses a separate browser flow. JavaScript receives and stores only the short-lived access token. The refresh token is placed in an `HttpOnly`, `SameSite=Strict` cookie scoped to `/api/auth/web`; the legacy session-storage key is removed. Concurrent browser refresh attempts are serialized so token rotation does not create accidental refresh-token reuse. Logout revokes the cookie-backed refresh token when available and clears the cookie.

## Reverse-proxy trust boundary

Forwarded headers are accepted only when `ReverseProxy:TrustForwardedHeaders` is enabled and at least one trusted proxy IP or CIDR network is configured. Startup fails instead of trusting arbitrary proxies when the allow-list is empty or malformed.

The production Docker deployment uses a dedicated internal backend CIDR (`BACKEND_NETWORK_CIDR`, default `10.77.0.0/24`). Only the edge proxy and backend services are attached to that network, and the API trusts forwarded headers from that configured network. Forwarded-header processing is limited to one hop and requires header symmetry.

Caddy remains the trusted HTTPS edge and forwards `X-Forwarded-Proto: https` to the API.

## Rate limiting

The following fixed-window limits are enforced before application actions run:

- login/refresh endpoints: 10 requests per minute per remote IP;
- centralized agent enrollment: 5 requests per 5 minutes per remote IP;
- centralized device plan/status calls: 30 requests per minute per device ID (falling back to remote IP if no device route value is available).

Rate-limit rejection uses HTTP 429 and does not queue excess requests.

## Edge security headers

The Admin Web edge sends HSTS, Content Security Policy, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`, `Permissions-Policy`, `Cross-Origin-Opener-Policy`, and `X-Permitted-Cross-Domain-Policies: none`.

API, hub, and OpenAPI paths receive `Cache-Control: no-store`. The update manifest remains `no-store`; immutable versioned ZIP packages retain long-lived immutable caching.

The current CSP allows only same-origin application resources, same-origin WebSocket/API connections, data images, and inline styles required by the existing Admin Web bundle. It blocks objects, embedding, alternate base URLs, and cross-origin form submission.

## Secret-file baseline

Production secret initialization uses `umask 077`, creates secret directories with mode `700`, and enforces mode `600` on PostgreSQL, database-connection, JWT-signing, and agent-enrollment secret files. Symbolic-link secret paths are rejected. Existing files are revalidated and have owner-only read/write permissions enforced rather than preserving weaker modes.

Secret values are supplied to the API through Docker secrets / key-per-file configuration. They are not placed in the committed production environment template.

## Permission audit baseline

The permission catalog separates read and manage capabilities across users, employees, departments, roles, shifts, projects, tasks, surveys, reports, operations, centralized agent updates, presence, monitoring, access assignments, and audit logs.

Sensitive write boundaries remain explicit. Examples include:

- employee create/update requires both `employees.manage` and `roles.manage` because those operations can assign roles;
- incident acknowledge/assign/resolve requires `operations.manage`, while operational views require `reports.read`;
- centralized rollout create/pause/resume/cancel/promote requires `agent-updates.manage`, while rollout/device overview requires `reports.read`;
- audit-log access requires `audit.read`;
- monitoring policy changes require `monitoring.manage`;
- access-assignment mutation requires `access.assignments.manage`.

Default roles are `SuperAdmin`, `Admin`, `HR`, `ProjectManager`, `TeamLead`, `SurveySupervisor`, `Developer`, `Surveyor`, and `Employee`. The bootstrap seeding process assigns every permission only to `SuperAdmin`. It does not automatically grant broad permissions to the other roles; their organization-specific permissions must be assigned deliberately through role management.

## CI security acceptance

CI locks the baseline with regression tests for the authenticated fallback policy, every registered permission policy, the exact anonymous controller surface, and trusted forwarded-header configuration.

Production acceptance also verifies that the Admin Web security headers are present, production OpenAPI is not exposed, secret files are regular files with mode `600`, the update manifest is served with no-store/nosniff protections, and the existing published package size/SHA-256 checks still pass.

## Residual security considerations

An access token remains a bearer credential until its short expiration and the API does not perform a database revocation lookup on every authenticated request. Admin Web therefore keeps the access token short-lived and limits the longer-lived refresh secret to an HttpOnly cookie.

The browser refresh-cookie security model assumes the documented HTTPS edge deployment. Deployments that bypass the trusted reverse proxy or weaken HTTPS/CSP controls are outside this production baseline.

Production Windows release signing remains a separate control: CI bundles are unsigned development/acceptance artifacts unless real signing certificate material is supplied to the release pipeline.
