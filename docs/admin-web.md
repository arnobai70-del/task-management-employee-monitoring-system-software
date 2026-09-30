# Admin Web

## Scope

`src/AdminWeb` is the browser-based administration console for the central Task Management & Employee Monitoring API. It uses server-issued permissions for presentation while the backend remains authoritative for all reads and mutations.

Implemented areas include:

- Dashboard — reporting metrics plus permission-gated live workforce presence and Website Work Needs Attention signals.
- Employee Productivity — daily/weekly Website Work productivity with assignment/start/review/approval metrics, working-time reconstruction and CSV export.
- My Follow-ups — manager-specific pending/overdue Website Work attention follow-ups with owner-only resolution, realtime refresh and a global count badge.
- Employees and departments — directory plus management workflows.
- Attendance and shifts — work-session visibility and shift administration.
- Projects and tasks — project/task management workflows.
- Website Work — external website target assignment, live worker progress, completion review and approved-completion history.
- RDP, IP and website assignments — company access-assignment workflows.
- Surveys — external survey-link assignment; survey questions and answers remain on the assigned third-party website.
- Audit Logs — permission-gated administrative audit history.

Backend business rules must not be duplicated or weakened in the browser.

## Technology

- React 19
- TypeScript with strict type checking
- Vite
- React Router
- `@microsoft/signalr` for live workforce, Website Work review and manager follow-up updates
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

## Website Work productivity reporting

`/productivity` requires `reports.read` and calls `GET /api/reports/website-work/productivity`.

The page supports a server-validated date range of up to 366 days and daily or calendar-week trend grouping. The browser sends its UTC offset so day boundaries match the administrator's local reporting day instead of the API server's timezone.

The report includes:

- Website Work targets assigned inside the selected period;
- targets first started inside the period;
- working time reconstructed from `Start/Open` or manager `reopen` until employee submission/final completion;
- completion-review submissions;
- final manager-approved completions;
- correction/reopen actions;
- unresolved overdue work at each period end; and
- cohort completion rate for work assigned in the period and approved by that period's end.

Employee rows are grouped by the currently assigned employee. The page can filter the returned rows locally by employee code/name/department and export the employee table as UTF-8 CSV. Export does not contain external website page content, passwords, cookies, balances or form data.

## Manager follow-up inbox

`/follow-ups` requires `tasks.manage` and calls `GET /api/website-work/follow-ups/mine` for the authenticated manager. It displays only current follow-ups assigned to that account after the latest Website Work lifecycle event.

The page separates pending and overdue follow-ups, can optionally show resolved items, and lets the current owner mark a follow-up resolved with an optional resolution note. Resolution is server-authorized and audit logged; a manager cannot resolve another manager's current follow-up.

Follow-up assignment, reassignment, resolution and invalidation are pushed over the existing SignalR hub to a user-specific group for the affected manager account. The Admin Web displays a short toast, refreshes **My Follow-ups** immediately, and maintains a global Follow-ups badge showing the manager's pending + overdue count. A periodic 15/20-second refresh remains enabled as a resilient fallback and to detect a due-time transition from Pending to Overdue when no database write occurs; a newly overdue item raises an overdue toast.

Dashboard Needs Attention separately reports pending and overdue follow-up totals and links managers to the inbox. A resolved follow-up remains managed until the Website Work lifecycle changes, preventing the same warning from immediately reappearing after it has been handled. The durable `TaskActivity`-derived inbox remains authoritative if a realtime delivery is missed.

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
| `/productivity` Website Work productivity | `reports.read` |
| Dashboard live workforce panel | `presence.read` |
| `/employees` | `employees.read` |
| `/departments` | `departments.read` |
| `/shifts` | `shifts.read` |
| `/attendance` | `attendance.read` |
| `/projects` | `projects.read` |
| `/tasks` | `tasks.read` |
| `/website-work` | `tasks.read` |
| Website Work assign/edit/review | `tasks.manage` |
| `/follow-ups` | `tasks.manage` |
| `/surveys` | `surveys.read` |
| `/access/rdp`, `/access/ip`, `/access/websites` | `access.assignments.read` |
| `/audit-logs` | `audit.read` |

A signed-in user without permission for a requested section receives the Admin Web `403` view. Optional panels such as live presence simply do not render/connect without their dedicated permission. These checks are UX controls only; backend authorization is still mandatory.

## Deployment notes

- Serve the built static files over HTTPS.
- Prefer serving the UI, REST API and SignalR hub from the same trusted origin or behind one reverse proxy.
- Configure reverse proxies/load balancers to support WebSocket upgrades for `/hubs/realtime`.
- Do not embed database credentials, JWT signing keys, bootstrap passwords or other backend secrets in `VITE_*` variables; Vite build-time variables are public to the browser.
- Use a strict Content Security Policy in the web server/reverse proxy.
- Keep third-party scripts out of authenticated pages unless explicitly reviewed.
- Preserve backend audit logging and authorization checks for every mutation workflow.
