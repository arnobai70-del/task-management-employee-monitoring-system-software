# Website Work Needs Attention

The Dashboard surfaces Website Work that needs manager attention without collecting content from the external website itself.

## Signals

The server evaluates unresolved Website Work and raises one combined alert per target when one or more configured conditions apply:

- overdue target — Critical;
- continuously Working beyond `WebsiteWorkAttention:LongWorkingMinutes` — High;
- Pending Review beyond `WebsiteWorkAttention:PendingReviewMinutes` — High;
- correction/reopen count at or above `WebsiteWorkAttention:RepeatedCorrectionCount` — Medium, escalating to High at twice the threshold.

The browser sends its UTC offset so overdue day boundaries match the manager's local reporting day.

## Manager actions

Reading the Dashboard and attention feed requires `reports.read`. Mutation controls require `tasks.manage`.

Managers can:

- **Acknowledge** — suppress the current alert until the Website Work lifecycle changes. A later configure/start/submit/reopen/approve/complete lifecycle event makes a still-relevant signal active again.
- **Snooze** — suppress the alert for 15 minutes to 7 days. If the target is still unresolved when the snooze expires, it automatically returns to the active attention list.
- **Assign follow-up** — select an active user who has `tasks.manage`, add a required follow-up note, and set a due time between 5 minutes and 30 days in the future. The alert is suppressed until that due time. If the target is still unresolved when follow-up becomes due, it automatically resurfaces while retaining the owner, note and due-time context.

The Dashboard hides managed alerts by default so the same warning does not keep distracting managers. **Show managed** displays currently acknowledged, snoozed and follow-up-managed signals. Active/managed counts are returned independently from the visible row limit.

## Persistence and audit

No new table is required. Attention actions are stored as existing `TaskActivity` records:

- `website-work.attention.acknowledged`
- `website-work.attention.snoozed`
- `website-work.attention.follow-up-assigned`

Each mutation also writes an `AuditLog` entry with the authenticated manager as actor. Because management state is attached to the Website Work activity stream, later lifecycle events naturally invalidate stale acknowledgements and follow-up suppression.

## API

Read endpoints:

- `GET /api/reports/website-work/attention?utcOffsetMinutes=...&limit=...&includeSuppressed=...` — `reports.read`
- `GET /api/website-work/attention/follow-up-owners` — `tasks.manage` (and the Website Work controller's read policy)

Mutation endpoints:

- `POST /api/website-work/{taskId}/attention/acknowledge`
- `POST /api/website-work/{taskId}/attention/snooze`
- `POST /api/website-work/{taskId}/attention/follow-up`

All mutations require `tasks.manage` and reject closed Website Work.

## Privacy boundary

Needs Attention uses only internal task/lifecycle metadata already stored by the Website Work workflow. It does not capture external website page/form content, passwords, cookies, balances, earnings, browsing history or survey answers.
