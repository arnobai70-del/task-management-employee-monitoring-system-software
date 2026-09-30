# Employee Survey Workspace

The Windows employee client has a dedicated **Survey Work** tab for survey work that is hosted on an external website.

## Employee flow

1. An authorized administrator assigns an employee a survey title and an absolute HTTP/HTTPS website URL in the Admin Web.
2. The assignment can include optional start date, due date, and instructions.
3. If the employee is online, a durable survey notification is pushed over the existing SignalR employee channel and the **Survey Work** list refreshes automatically.
4. After sign-in, the employee opens **Survey Work** in the Windows client.
5. The employee selects an assignment and clicks **Open selected survey**.
6. The API re-validates ownership, active state, start date, and URL before the default browser is launched.
7. The employee completes the survey on the external website.

The desktop list shows `Ready`, `Scheduled`, `Overdue`, or `Inactive` as a local presentation state. An overdue active assignment can still be opened unless the administrator deactivates it.

## Realtime notifications

Survey link creation, meaningful updates, deactivation/reactivation, and reassignment create durable employee notifications. Reassignment notifies the previous employee that the survey was removed and the new employee that it was assigned.

Notifications are saved in `employee_notifications` in the same database save as the survey-link change, then published through the existing employee SignalR group after the save succeeds. If realtime delivery is temporarily unavailable, the durable notification remains available in the employee Notifications tab.

The Windows client refreshes its notification list and Survey Work list when a `SurveyLink` realtime notification arrives. This does not require polling the survey endpoint to discover a newly assigned survey while the realtime connection is healthy.

## Data boundary

The task-monitoring system does not author the external questionnaire and does not read or store survey answers, page content, cookies, form fields, passwords, or browser history.

When the employee opens an assigned survey, the backend writes an audit event with assignment identity and hostname-only URL metadata. URL path, query string, fragment, and external form content are not written to that audit event.

Survey-classified links are intentionally excluded from **My Access → Website** so employees see each survey assignment in one place only.

## Employee API

- `GET /api/me/survey-links?includeInactive=false` returns survey links assigned to the authenticated employee.
- `POST /api/me/survey-links/{id}/open` validates the assignment and records the open audit event before the client launches the returned URL.

Both endpoints resolve the authenticated account to an active employee profile; an employee cannot open another employee's survey assignment through these routes.
