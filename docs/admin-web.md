# Admin Web

## Scope

`src/AdminWeb` is the browser-based administration console for the central Task Management & Employee Monitoring API. The first milestone is intentionally read-focused: it establishes authenticated navigation, permission boundaries, responsive layout, dashboard reporting, and operational list views before mutation-heavy administration forms are added.

Implemented views:

- Dashboard — workforce, attendance, project, task, survey and workload signals from `/api/reports/*`.
- Employees — active employee directory from `/api/employees`.
- Attendance — organization work-session history from `/api/attendance`.
- Projects — project summaries from `/api/projects`.
- Tasks — cross-project task queue from `/api/tasks`.
- Surveys — project-scoped survey form list from `/api/surveys`.

Create/edit/deactivate, shift administration, project membership/task mutation, survey assignment/review and other write workflows remain a separate Admin Web management milestone. Backend business rules must not be duplicated or weakened in the browser.

## Technology

- React 19
- TypeScript with strict type checking
- Vite
- React Router
- Native CSS for the responsive application shell and data visualization

The frontend deliberately avoids a large UI or chart framework in this foundation so permission/session behavior and API integration stay easy to audit.

## Authentication and authorization

The backend returns a short-lived JWT access token and an opaque rotating refresh token. The Admin Web stores the current session in `sessionStorage`, not persistent `localStorage`, so closing the browser tab/session removes the local token copy.

The access token is decoded only to drive presentation decisions such as navigation visibility. Server authorization remains authoritative. Every protected API request still sends the access token to the backend, and a hidden route cannot grant access if the backend permission policy rejects it.

The client:

1. signs in through `POST /api/auth/login`;
2. reads server-issued `permission` and role claims from the access token;
3. hides navigation entries the user cannot read;
4. guards routes using the same permission codes used by backend policies;
5. refreshes the session when the access token is near expiry or after one `401` response;
6. stores the rotated refresh token returned by the backend;
7. clears the browser session if refresh fails; and
8. calls `POST /api/auth/logout` when the user signs out.

Because the current backend contract returns refresh tokens in JSON, JavaScript must be able to access the refresh token. A future authentication design may move refresh-token transport to a secure same-site HttpOnly cookie, but that requires an explicit backend contract change. Until then, avoiding persistent browser storage and preventing XSS remain important deployment requirements.

## Local development

Install dependencies and run the Vite development server:

```bash
cd src/AdminWeb
npm install
npm run dev
```

The development server listens on port `5173` and proxies `/api` to `http://localhost:5080` by default. To point the proxy at a different local API address, set:

```bash
VITE_DEV_API_TARGET=https://localhost:7001 npm run dev
```

The application uses relative `/api` requests by default. For a deployment where the API is intentionally hosted on another origin, set `VITE_API_BASE_URL` at build time and configure the backend/network layer with an explicit trusted-origin policy. Same-origin deployment is preferred because it avoids broad browser CORS exposure.

## Production build

```bash
cd src/AdminWeb
npm install --no-audit --no-fund
npm run build
```

`npm run build` performs strict TypeScript checking and then creates the production Vite bundle in `src/AdminWeb/dist`.

GitHub Actions runs the Admin Web build independently from the backend job. A frontend change is not merge-ready unless both frontend and backend jobs are green.

## Permission-to-route mapping

| Route | Required permission |
| --- | --- |
| `/dashboard` | `reports.read` |
| `/employees` | `employees.read` |
| `/attendance` | `attendance.read` |
| `/projects` | `projects.read` |
| `/tasks` | `tasks.read` |
| `/surveys` | `surveys.read` |

A signed-in user without permission for a requested section receives the Admin Web `403` view. Navigation is generated from the same permission map, so unavailable sections are not advertised.

## Deployment notes

- Serve the built static files over HTTPS.
- Prefer serving the UI and API from the same trusted origin or behind one reverse proxy.
- Do not embed database credentials, JWT signing keys, bootstrap passwords or other backend secrets in `VITE_*` variables; Vite build-time variables are public to the browser.
- Use a strict Content Security Policy in the web server/reverse proxy.
- Keep third-party scripts out of authenticated pages unless explicitly reviewed.
- Preserve backend audit logging and authorization checks for every future mutation workflow.
