# Website Work / Target Tasks

Website Work is for concrete work targets that an employee completes on an external website. It is separate from Survey Work.

## Example

An administrator can assign:

- worker: `Jihad`
- target: `InboxDollars - complete $5 target`
- website: the company-approved InboxDollars work URL
- optional instructions, priority, and due date

The worker sees the item in the Windows **Website Work** tab.

## Lifecycle

1. The administrator assigns the target to an active member of a project.
2. The worker selects the target and clicks **Start / Open selected**.
3. The backend validates that the authenticated employee owns the assignment and that the URL is a safe HTTP/HTTPS URL.
4. Before the browser is launched, the task changes from `ToDo` to `InProgress`. The Windows UI presents these states as **Ready** and **Working**.
5. The assigned URL opens in the worker's default browser.
6. The worker completes the external target on that website.
7. The worker returns to the Windows client and clicks **Mark Complete**.
8. The backend changes the task to `Done`, stores the completion timestamp and a durable completion activity, and publishes a realtime manager event.
9. Authorized task readers/managers receive an Admin Web notification such as `Jihad completed: InboxDollars - complete $5 target`.
10. The Admin Web **Website Work** page keeps a durable recent-completion list even if the manager was offline when the realtime event happened.

Re-opening an item that is already Working keeps it Working and opens the same assigned website again. Completed or cancelled items cannot be started again through the employee Website Work workflow.

## Administration

Admin Web exposes **Website Work** to accounts with `tasks.read`. Creating or editing an assignment requires `tasks.manage`.

Assignments reuse the existing project/task authorization model:

- the project must not be completed or archived;
- the worker must be active;
- the worker must be an active member of the selected project;
- duplicate open targets for the same employee/project/title are rejected;
- due dates must stay inside the project date range when project dates are configured;
- HTTP/HTTPS URLs with embedded `user:password@host` credentials are rejected.

## Storage and data boundary

Website Work intentionally reuses the existing `ProjectTask` and `TaskActivity` tables, so this feature does not require a new database schema or migration.

The exact administrator-assigned work URL is stored as work configuration so the employee can reopen it. Start/open audit events record hostname-level context rather than scraped page data. The system does not inspect or store the external site's page contents, form fields, cookies, passwords, account balance, earnings, or browser history.

A completion record means that the assigned employee explicitly selected **Mark Complete**. It is a workflow acknowledgement, not automatic proof that the external website itself verified the target.
