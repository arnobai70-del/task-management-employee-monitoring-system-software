# Automated security alerting and escalation

The central API converts existing security audit signals into durable, deduplicated security alerts. This feature does not collect new employee-monitoring data. Its inputs are authentication audit events and rate-limit rejection audit events already produced by the API.

## Signals

The scanner evaluates the configured correlation window and produces alerts for:

- repeated failed or blocked sign-ins grouped by source IP;
- repeated HTTP rate-limit rejections grouped by source IP; and
- refresh-token reuse detections grouped by affected user when available, otherwise by source IP.

`SecurityObservability:FailedLoginThreshold` and `SecurityObservability:RateLimitThreshold` control burst detection. A burst at twice the configured threshold is Critical; refresh-token reuse is always Critical.

## Durable alert lifecycle

Security alerts are persisted as immutable `AuditLog` events with target type `SecurityAlert`. A deterministic ID derived from alert kind and source key suppresses duplicate alerts across scans.

Alert status is one of `Open`, `Acknowledged`, `Escalated`, or `Resolved`.

- A new correlated signal creates an Open alert.
- A manager with `security.alerts.manage` can acknowledge, assign, or manually resolve the alert.
- An unacknowledged Critical alert becomes Escalated after `SecurityObservability:AlertEscalationAfterMinutes`.
- When the correlated signal is no longer active, the scanner auto-resolves the alert as recovered.
- A resolved alert can reopen after `SecurityObservability:AlertReopenCooldownMinutes` when a new matching signal is observed.

Every lifecycle transition is an audit event and includes actor information and optional operator notes when a user performs the action.

## Realtime notifications

Users with `audit.read` join the `security-alert-readers` SignalR group. Alert detection, update, escalation, acknowledgement, assignment, resolution, and recovery events are published as `securityAlertChanged` messages. The Admin Web console displays a realtime toast for important changes and keeps a persistent active-alert counter with polling as a fallback.

Realtime delivery is advisory; the audit-backed alert state is authoritative if a browser is offline or SignalR reconnects.

## Permissions

- `audit.read`: view security alerts and receive realtime alert notifications.
- `security.alerts.manage`: acknowledge, assign, and resolve security alerts.

Alert assignees must be active users with both permissions. `SuperAdmin` receives newly defined permissions through the existing permission seed process; no other business role is broadened automatically.

## Configuration

The default application configuration uses:

- correlation window: 15 minutes;
- failed-login threshold: 5;
- rate-limit threshold: 3;
- alert scan interval: 60 seconds;
- critical acknowledgement/escalation SLA: 5 minutes; and
- reopen cooldown: 15 minutes.

All values are startup-validated. Invalid scan intervals, escalation SLAs, thresholds, or cooldowns fail configuration validation rather than silently falling back to unsafe values.

## Privacy boundary

Security alerts contain operational security metadata only: alert kind, severity, source key, aggregate event count, timestamps, status, owner, audit actor data, and operator notes. This feature does not collect or export screenshots, screen contents, keystrokes, passwords, cookies, refresh-token values, browsing history, survey answers, balances, earnings, or unrelated employee activity.
