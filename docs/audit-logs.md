# Audit log console

The audit log console provides read-only, permission-gated access to security and administrative events already recorded by the API.

## Authorization

The API endpoint and Admin Web route require the `audit.read` permission. The backend remains authoritative; hiding the navigation item in the browser is only a usability measure and does not replace server-side authorization.

## API

`GET /api/audit-logs`

Supported query parameters:

- `search` — case-insensitive match against action, target type, target ID, or source IP address.
- `action` — exact case-insensitive action filter, for example `employee.updated`.
- `targetType` — exact case-insensitive target type filter, for example `Employee`.
- `actorUserId` — exact actor user ID filter.
- `fromUtc` / `toUtc` — optional UTC timestamp bounds. A reversed range returns HTTP 400.
- `page` — defaults to 1 and is clamped to at least 1.
- `pageSize` — defaults to 50 and is clamped to 1–100.

Results are ordered newest-first and returned using the common paged response contract. When the actor still has a user account, the response resolves the actor email for operator readability. Historical audit rows are not rewritten if the actor account later changes.

## Admin Web

Authorized administrators can open **Audit Logs** from the primary navigation. The view supports:

- free-text search;
- exact action and target-type filters;
- UTC date range filtering;
- 50-row server-side pagination;
- manual refresh;
- expandable metadata and user-agent details.

Audit entries are never editable or deletable through this interface.

## Data handling

Audit metadata may contain operational identifiers or change details written by existing business services. Access must therefore remain limited to trusted roles with `audit.read`. The console does not add new audit collection, change retention, expose passwords, or persist additional client-side copies of audit data.

## Validation

Backend tests cover filtering, newest-first ordering, actor-email resolution and page-size bounds. The Admin Web build/type-check gate validates the route and UI integration in CI.
