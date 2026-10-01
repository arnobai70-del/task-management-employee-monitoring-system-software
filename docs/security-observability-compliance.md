# Security observability and audit/compliance export

This milestone projects security and compliance views from the existing `AuditLog` record. It does not introduce a parallel employee-monitoring data store and it does not broaden monitoring collection.

## Security dashboard

The Admin **Audit Logs** area includes a security observability summary for a selectable one-hour to seven-day window. It reports failed and blocked sign-ins, successful sign-ins, refresh-token reuse detections, rate-limit rejections, recent authenticated privileged actions, unique source IPs, recent security events, and top source IPs.

Correlation is threshold-based and non-destructive. The default 15-minute correlation window highlights:

- five or more failed/blocked sign-ins from the same source (`FailedLoginBurst`),
- three or more rate-limit rejections from the same source (`RateLimitBurst`), and
- any refresh-token reuse detection (`RefreshTokenReuse`, critical).

The correlation view is derived from audit events and does not create a second incident state machine. This avoids conflicting ownership with the production health incident scanner while still giving administrators a single place to investigate related security events.

## Rate-limit auditing

HTTP 429 responses from the configured ASP.NET Core rate-limit policies are recorded as `security.rate_limit.rejected` audit events. The event stores only operational request metadata: endpoint method/path, source IP, user-agent, and timestamp. It does not record request bodies, passwords, cookies, refresh tokens, page content, screenshots, keystrokes, survey answers, or browsing history.

## Privileged-action reporting

The dashboard treats authenticated non-`auth.*` and non-`security.*` audit events as privileged/admin activity for reporting purposes. The underlying immutable audit action and target remain visible so administrators can apply exact audit-log filters when investigating a specific permission or workflow.

## Audit integrity fingerprint

For the selected dashboard window the API orders audit records deterministically by timestamp and ID, canonicalizes the stored audit fields, and computes a SHA-256 fingerprint. The dashboard also reports structural anomalies where an audit record is missing its action or target type.

The fingerprint is a deterministic snapshot checksum, not a cryptographic append-only ledger or external notarization service. It can verify that two exports/snapshots contain the same canonical audit records, but it does not by itself prove that a database administrator never altered historical data.

## Retention coverage

`SecurityObservability:MinimumAuditRetentionDays` defaults to 90 days. The dashboard reports the age of the oldest available audit record and whether the currently available history covers that minimum. A new installation can therefore show **Building** until enough history naturally exists. This milestone never deletes audit records automatically.

Default settings:

```text
SecurityObservability:DefaultWindowHours = 24
SecurityObservability:MaxWindowHours = 168
SecurityObservability:CorrelationWindowMinutes = 15
SecurityObservability:FailedLoginThreshold = 5
SecurityObservability:RateLimitThreshold = 3
SecurityObservability:MinimumAuditRetentionDays = 90
SecurityObservability:ExportMaxRecords = 50000
```

## Compliance CSV export

`GET /api/security-observability/audit-export.csv` exports up to the configured record limit for a requested range (maximum 366 days per request). If dates are omitted, the last 30 days are exported.

The response includes:

- `X-Audit-SHA256` — SHA-256 of the exact CSV bytes,
- `X-Audit-Record-Count` — exported row count, and
- `X-Audit-Truncated` — `true` if the configured export limit was reached.

Export is protected by the dedicated `audit.export` permission. Viewing the dashboard and ordinary audit history remains protected by `audit.read`. The normal permission seeding flow keeps new permissions within the existing least-privilege model; roles are not automatically broadened except for the existing SuperAdmin all-permissions seed behavior.

## Privacy boundary

The security dashboard and compliance export operate only on existing audit metadata. They do not export passwords, password hashes, access tokens, refresh tokens, cookies, screenshots, screen content, keystrokes, external website pages/forms, balances, earnings, browsing history, or survey answers.
