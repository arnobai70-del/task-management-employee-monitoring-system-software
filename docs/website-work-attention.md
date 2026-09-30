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

Reading the Dashboard and attention feed requires `reports.read`. Attention mutations and the manager follow-up inbox require `tasks.manage`.

Managers can:

- **Acknowledge** — suppress the current alert until the Website Work lifecycle changes. A later configure/start/submit/reopen/approve/complete lifecycle event makes a still-relevant signal active again.
- **Snooze** — suppress the alert for 15 minutes to 7 days. If the target is still unresolved when the snooze expires, it automatically returns to the active attention list.
- **Assign follow-up** — select an active user who has `tasks.manage`, add a required follow-up note, and set a due time between 5 minutes and 30 days in the future. The alert is suppressed until that due time. If the target is still unresolved when follow-up becomes due, it automatically resurfaces while retaining the owner, note and due-time context.

The Dashboard hides managed alerts by default so the same warning does not keep distracting managers. **Show managed** displays acknowledged, snoozed, follow-up-managed and resolved signals. Active/managed counts are returned independently from the visible row limit, and the Dashboard separately reports pending and overdue follow-up counts.

## My Follow-ups

`/follow-ups` is the permission-gated manager inbox for the authenticated `tasks.manage` account. It only returns the current follow-up assignments owned by that user after the latest Website Work lifecycle event.

The inbox shows:

- Pending follow-ups whose due time is still in the future;
- Overdue follow-ups whose due time has passed;
- optional resolved follow-ups for the current Website Work lifecycle;
- worker, target, project and current work state;
- who assigned the follow-up, the required follow-up note and due time; and
- resolution time and optional resolution note for resolved items.

Only the manager currently assigned to a follow-up can mark it **Resolved**. Resolution suppresses the current attention signal so it does not immediately reappear after the manager has handled it. A later Website Work lifecycle action invalidates that resolution; if the target still crosses an attention threshold after the lifecycle changes, it can surface again normally.

If a follow-up is reassigned, acknowledged or snoozed after assignment, the replaced follow-up is no longer considered current and disappears from the former owner's inbox.

## Realtime follow-up delivery

Every authenticated SignalR connection joins a user-specific realtime group derived from the server-validated JWT subject. This allows manager follow-up events to target the assigned manager account even when that account is not linked to an employee profile.

After a successful database save, follow-up lifecycle changes publish `websiteWorkFollowUpChanged` to the affected manager:

- assignment sends **Assigned**;
- assigning the same manager again with changed note/due time sends **Updated**;
- reassignment sends **Removed** to the former manager and **Assigned** to the new manager;
- owner resolution sends **Resolved**; and
- acknowledgement, snooze, or a Website Work lifecycle change sends **Removed** when it makes the previous follow-up no longer current.

The Admin Web shows a global Follow-ups badge, displays a short realtime toast, and refreshes **My Follow-ups** immediately. The authoritative inbox is still rebuilt from durable `TaskActivity` data, so realtime delivery is not the source of truth. The browser also refreshes follow-up state periodically; this is required for due-time transitions because a Pending follow-up can become Overdue simply as time passes without any database mutation. A newly overdue item produces an overdue toast and updates the badge.

## Persistence and audit

No new table is required. Attention actions are stored as existing `TaskActivity` records:

- `website-work.attention.acknowledged`
- `website-work.attention.snoozed`
- `website-work.attention.follow-up-assigned`
- `website-work.attention.follow-up-resolved`

Each mutation also writes an `AuditLog` entry with the authenticated manager as actor. Because management state is attached to the Website Work activity stream, later lifecycle events naturally invalidate stale acknowledgements, follow-up suppression and resolutions.

## API

Read endpoints:

- `GET /api/reports/website-work/attention?utcOffsetMinutes=...&limit=...&includeSuppressed=...` — `reports.read`
- `GET /api/website-work/attention/follow-up-owners` — `tasks.manage` (and the Website Work controller's read policy)
- `GET /api/website-work/follow-ups/mine?includeResolved=...` — `tasks.manage`

Mutation endpoints:

- `POST /api/website-work/{taskId}/attention/acknowledge`
- `POST /api/website-work/{taskId}/attention/snooze`
- `POST /api/website-work/{taskId}/attention/follow-up`
- `POST /api/website-work/follow-ups/{taskId}/resolve`

Attention mutations require `tasks.manage`; the assign/acknowledge/snooze routes also retain the Website Work controller's read policy. Closed Website Work rejects attention mutations.

## Privacy boundary

Needs Attention and My Follow-ups use only internal task/lifecycle metadata already stored by the Website Work workflow. They do not capture external website page/form content, passwords, cookies, balances, earnings, browsing history or survey answers.
