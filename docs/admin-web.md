# Admin Web

## Scope

`src/AdminWeb` is the browser-based administration console for the central Task Management & Employee Monitoring API. It uses server-issued permissions for presentation while the backend remains authoritative for all reads and mutations.

Implemented areas include:

- Dashboard — reporting metrics plus permission-gated live workforce presence.
- Employees and departments — directory plus management workflows.
- Attendance and shifts — work-session visibility and shift administration.
- Projects and tasks — project/task management workflows.
- RDP, IP and website assignments — company access-assignment workflows.
- Surveys — current read view for project-scoped survey forms; survey management write UI is not part of the current Admin Web roadmap.

Backend business rules must not be duplicated or weakened in the browser.

## Technology

- React 19
- TypeScript with strict type checking
- Vite
- React Router
- `@microsoft/signalr` for live workforce updates
- Native CSS for the responsive application shell and data visualization

## Authentication and authorization

The backend returns a short-lived JWT access token and an opaque rotating refresh token. The Admin Web stores the current session in `sessionStorage`, not persistent `localStorage`, so closing the browser tab/session removes the local token copy.

The access token is decoded only to drive presentation decisions such as navigation visibility and whether optional panels should connect. Server authorization remains authoritative. Every protected REST request and SignalR handshake still requires a valid backend-issued token.

The client:

1. signs in through `POST /api/auth/login`;
2. reads server-issued `permission` and role claims from the access token;
3. hides navigation/write controls the user cannot use;
4. guards routes and optional live panels using the same permission codes used by backend policies;
5. refreshes the session when the access token is near expiry or after one `401` response;
6. stores the rotated refresh token returned by the backend;
7. supplies a refresh-aware access-token factory to SignalR reconnects;
8. clears the browser session if refresh fails; and
9. calls `POST /api/auth/logout` when the user signs out.

Because the current backend contract returns refresh tokens in JSON, JavaScript must be able to access the refresh token. A future authentication design may move refresh-token transport to a secure same-site HttpOnly cookie, but that requires an explicit backend contract change. Until then, avoiding persistent browser storage and preventing XSS remain important deployment requirements.

## Live workforce presence

The Dashboard renders the Live Workforce panel only when the authenticated account has `presence.read`.

The panel uses two complementary channels:

- `GET /api/presence?page=1&pageSize=100` for the authoritative roster snapshot.
- `/hubs/realtime` for SignalR `presenceChanged` events.

The REST snapshot is refreshed every 30 seconds even while SignalR is connected. This is deliberate: online/offline is based on heartbeat age, so a client that crashes or loses connectivity cannot emit its own offline event. The periodic authoritative refresh lets the server's heartbeat timeout transition that employee to Offline.

Displayed work state is server-derived:

- Offline — no recent heartbeat.
- Working — recent heartbeat plus open work session.
- OnBreak — recent heartbeat plus open work session with an open break.
- Idle — recent heartbeat without an open work session.

The browser does not calculate or submit employee work state.

## Local development

Install dependencies and run the Vite development server:

```bash
cd src/AdminWeb
npm install
npm run dev
```

The development server listens on port `5173` and proxies both `/api` and `/hubs` to `http://localhost:5080` by default. `/hubs` enables WebSocket proxying for SignalR. To point the proxy at a different local API address, set:

```bash
VITE_DEV_API_TARGET=https://localhost:7001 npm run dev
```

The application uses relative API/hub paths by default. For a deployment where the API is intentionally hosted on another origin, set `VITE_API_BASE_URL` at build time and configure the backend/network layer with an explicit trusted-origin policy. Same-origin deployment is preferred because it avoids broad browser CORS exposure.

## Production build

```bash
cd src/AdminWeb
npm install --no-audit --no-fund
npm run build
```

`npm run build` performs strict TypeScript checking and then creates the production Vite bundle in `src/AdminWeb/dist`.

GitHub Actions runs the Admin Web build independently from backend and Windows-client jobs. A frontend change is not merge-ready unless all applicable jobs are green.

## Permission boundaries

Important route/panel boundaries include:

| Area | Required permission |
| --- | --- |
| `/dashboard` reporting | `reports.read` |
| Dashboard live workforce panel | `presence.read` |
| `/employees` | `employees.read` |
| `/departments` | `departments.read` |
| `/shifts` | `shifts.read` |
| `/attendance` | `attendance.read` |
| `/projects` | `projects.read` |
| `/tasks` | `tasks.read` |
| `/surveys` | `surveys.read` |
| `/access/rdp`, `/access/ip`, `/access/websites` | `access.assignments.read` |

A signed-in user without permission for a requested section receives the Admin Web `403` view. Optional panels such as live presence simply do not render/connect without their dedicated permission. These checks are UX controls only; backend authorization is still mandatory.

## Deployment notes

- Serve the built static files over HTTPS.
- Prefer serving the UI, REST API and SignalR hub from the same trusted origin or behind one reverse proxy.
- Configure reverse proxies/load balancers to support WebSocket upgrades for `/hubs/realtime`.
- Do not embed database credentials, JWT signing keys, bootstrap passwords or other backend secrets in `VITE_*` variables; Vite build-time variables are public to the browser.
- Use a strict Content Security Policy in the web server/reverse proxy.
- Keep third-party scripts out of authenticated pages unless explicitly reviewed.
- Preserve backend audit logging and authorization checks for every mutation workflow.
