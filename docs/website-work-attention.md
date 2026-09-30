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

## Durable notification center and reminders

The Follow-ups page contains a durable per-manager Notification Center. Follow-up assignment, update, reassignment/removal and resolution create notification projection records in the existing `TaskActivity` store. Each notification has its own unread/read state and remains available after logout or an offline period; SignalR is only the fast delivery path, not the source of truth.

A background server service independently checks current unresolved follow-ups. By default it scans every 60 seconds and creates one **Due soon** notification when a follow-up enters the final 30 minutes before its due time, then one **Overdue** notification if it remains current after the due time. `FollowUpReminders:DueSoonMinutes` and `FollowUpReminders:ScanIntervalSeconds` are startup-validated configuration values. Reminder identity is derived deterministically from the recipient, current follow-up assignment and notification kind, so repeated scans do not create duplicate reminders.

When a follow-up is resolved, reassigned, acknowledged, snoozed or invalidated by a later Website Work lifecycle event, that old assignment is no longer eligible for future reminder scans. A newly assigned follow-up receives a new reminder lifecycle.

The Admin Web exposes unread and total counts, an unread-only filter, **Mark read**, **Mark all read**, pagination and direct links back to My Follow-ups. Newly saved notifications are also pushed to the manager's user-specific SignalR group as `adminNotificationCreated`; missed realtime delivery does not remove the durable record.

No new database table or EF migration is required for this milestone because the existing `TaskActivity` persistence model already provides the task relationship, recipient user reference, JSON metadata and timestamps needed for the durable projection.

## Realtime follow-up delivery

Every authenticated SignalR connection joins a user-specific realtime group derived from the server-validated JWT subject. This allows manager follow-up events to target the assigned manager account even when that account is not linked to an employee profile.

After a successful database save, follow-up lifecycle changes publish `websiteWorkFollowUpChanged` to the affected manager:

- assignment sends **Assigned**;
- assigning the same manager again with changed note/due time sends **Updated**;
- reassignment sends **Removed** to the former manager and **Assigned** to the new manager;
- owner resolution sends **Resolved**; and
- acknowledgement, snooze, or a Website Work lifecycle change sends **Removed** when it makes the previous follow-up no longer current.

The Admin Web shows a global Follow-ups badge and refreshes **My Follow-ups** immediately. The durable Notification Center additionally provides the user-facing toast/history path. The authoritative inbox and notification history are stored server-side, while periodic browser refresh remains a resilience fallback for state display.

## Persistence and audit

No new table is required. Attention actions are stored as existing `TaskActivity` records:

- `website-work.attention.acknowledged`
- `website-work.attention.snoozed`
- `website-work.attention.follow-up-assigned`
- `website-work.attention.follow-up-resolved`

Durable manager notifications are also `TaskActivity` projection records using `admin.notification.unread` and `admin.notification.read`. Their deterministic IDs prevent duplicate projection rows for the same recipient/source/kind. Each attention mutation still writes an `AuditLog` entry with the authenticated manager as actor. Because management state is attached to the Website Work activity stream, later lifecycle events naturally invalidate stale acknowledgements, follow-up suppression and resolutions.

## API

Read endpoints:

- `GET /api/reports/website-work/attention?utcOffsetMinutes=...&limit=...&includeSuppressed=...` — `reports.read`
- `GET /api/website-work/attention/follow-up-owners` — `tasks.manage` (and the Website Work controller's read policy)
- `GET /api/website-work/follow-ups/mine?includeResolved=...` — `tasks.manage`
- `GET /api/admin-notifications?unreadOnly=...&page=...&pageSize=...` — `tasks.manage`
- `GET /api/admin-notifications/summary` — `tasks.manage`

Mutation endpoints:

- `POST /api/website-work/{taskId}/attention/acknowledge`
- `POST /api/website-work/{taskId}/attention/snooze`
- `POST /api/website-work/{taskId}/attention/follow-up`
- `POST /api/website-work/follow-ups/{taskId}/resolve`
- `POST /api/admin-notifications/{notificationId}/read`
- `POST /api/admin-notifications/read-all`

Attention and admin-notification mutations require `tasks.manage`; the assign/acknowledge/snooze routes also retain the Website Work controller's read policy. Closed Website Work rejects attention mutations.

## Privacy boundary

Needs Attention, My Follow-ups, durable notifications and due reminders use only internal task/lifecycle metadata already stored by the Website Work workflow. They do not capture external website page/form content, passwords, cookies, balances, earnings, browsing history or survey answers.
