# Employee Survey Workspace

The Windows employee client has a dedicated **Survey Work** tab for survey work that is hosted on an external website.

## Employee flow

1. An authorized administrator assigns an employee a survey title and an absolute HTTP/HTTPS website URL in the Admin Web.
2. The assignment can include optional start date, due date, and instructions.
3. After sign-in, the employee opens **Survey Work** in the Windows client.
4. The employee selects an assignment and clicks **Open selected survey**.
5. The API re-validates ownership, active state, start date, and URL before the default browser is launched.
6. The employee completes the survey on the external website.

The desktop list shows `Ready`, `Scheduled`, `Overdue`, or `Inactive` as a local presentation state. An overdue active assignment can still be opened unless the administrator deactivates it.

## Data boundary

The task-monitoring system does not author the external questionnaire and does not read or store survey answers, page content, cookies, form fields, passwords, or browser history.

When the employee opens an assigned survey, the backend writes an audit event with assignment identity and hostname-only URL metadata. URL path, query string, fragment, and external form content are not written to that audit event.

Survey-classified links are intentionally excluded from **My Access → Website** so employees see each survey assignment in one place only.

## Employee API

- `GET /api/me/survey-links?includeInactive=false` returns survey links assigned to the authenticated employee.
- `POST /api/me/survey-links/{id}/open` validates the assignment and records the open audit event before the client launches the returned URL.

Both endpoints resolve the authenticated account to an active employee profile; an employee cannot open another employee's survey assignment through these routes.
