# Website Work Completion Review

Website Work represents concrete targets completed on an administrator-assigned external website. The application stores the assignment and workflow state; it does not scrape or verify the external website's page content, credentials, cookies, form values, balances, earnings, or browsing history.

## Lifecycle

1. An administrator assigns a website target to an active project member.
2. The employee chooses **Start / Open selected** in the Windows client. The assignment becomes **Working** before the external website is launched.
3. When the employee believes the target is complete, they choose **Submit for Review**.
4. The assignment becomes **Pending Review** and the manager receives a realtime completion-submission notice with the employee name and target.
5. A manager with `tasks.manage` can:
   - **Approve** the submission, which makes the assignment final; or
   - **Reopen** it with a required correction comment.
6. Reopened work becomes **Correction Required** / working again. The employee receives a durable + realtime notification, sees the manager note in the Website Work tab, can reopen the assigned site, correct the work, and submit again.
7. Approval also creates a durable + realtime employee notification.

The worker cannot approve their own submission through employee endpoints. Manager review endpoints require `tasks.manage`.

## API

Employee endpoints:

- `GET /api/me/website-work`
- `POST /api/me/website-work/{id}/start`
- `POST /api/me/website-work/{id}/complete` — submits completion for review; the historical route name is retained for client compatibility.

Manager endpoints:

- `GET /api/website-work`
- `GET /api/website-work/progress?utcOffsetMinutes=...`
- `POST /api/website-work/{id}/approve`
- `POST /api/website-work/{id}/reopen`

## Storage compatibility

No database migration is required. The workflow reuses `ProjectTask`, `TaskActivity`, `EmployeeNotification`, and `AuditLog`:

- Working: `ProjectTaskStatus.InProgress`
- Pending Review: `ProjectTaskStatus.Blocked` plus `website-work.completed`
- Approved: `ProjectTaskStatus.Done` plus `website-work.approved`
- Correction Required: `ProjectTaskStatus.InProgress` plus `website-work.reopened`

Existing Website Work rows completed before this review feature remain final/approved when displayed, so rollout does not turn historical completed work into an unreviewable pending state.

## Dashboard semantics

The Admin Web dashboard shows:

- Working now
- Pending Review
- Submitted today
- Approved today
- Reopened today
- per-worker working/pending/submitted/approved/reopened counts
- live working duration, reset to the manager reopen time when correction work begins

"Today" uses the Admin Web browser's UTC offset rather than the API server timezone.

## Audit and notifications

Worker submission, manager approval, and manager reopen actions write audit/activity records. Reopen requires a correction comment. Employee status notifications are durable in the database and are also published over the existing authenticated SignalR employee channel; manager completion submissions continue to use the existing manager SignalR group.

## Data boundary

A submitted or approved completion is a workflow acknowledgement, not automated proof from the external website. The system intentionally does not collect external-site passwords, cookies, form fields, page content, account balances, earnings, or unrelated browsing history.
