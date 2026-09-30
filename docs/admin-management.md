# Admin Web Management Workflows

The Admin Web now uses the existing backend authorization model for read and write operations. Client-side permission checks only control presentation; the API remains authoritative.

## Employee and department administration

- Employees: search, create, edit, role assignment, department/supervisor assignment, employment status, and non-destructive deactivation.
- Employee create/update requires both `employees.manage` and `roles.manage` because role assignment can change privilege.
- Deactivation keeps history and relies on the backend to disable the login and revoke active refresh tokens.
- Departments: create, edit, activate/deactivate without destructive deletion.

## Shift administration

- Create/edit timezone-aware shifts.
- Assign active employees to active shifts with effective date ranges.
- Backend overlap/date/timezone rules remain authoritative.

## Project and task administration

- Projects: create/edit lifecycle state, dates and description.
- Project members: add/reactivate or remove active employees, with Member/Manager role.
- Member removal still respects the backend guard that blocks removal while open tasks remain assigned.
- Tasks: create/edit, assign only to active project members, and move through the backend-supported status transitions.
- Completing/archiving projects remains blocked by the backend while open tasks remain.

## Survey and field-operations administration

The Surveys section now exposes the existing survey backend as a complete permission-aware Admin Web workflow rather than a read-only list.

- `surveys.read` can view survey forms, questionnaire details and lifecycle state.
- `surveys.manage` can create draft surveys, edit draft metadata, replace the ordered questionnaire, publish drafts, close published surveys and archive eligible drafts/closed surveys.
- Question editing supports Text, LongText, Number, Boolean, Date, SingleChoice and MultipleChoice types, required flags, and choice options. Backend validation remains authoritative and questions lock after publication.
- `survey.assignments.read` can view field assignments and their latest revision/submission state.
- `survey.assignments.manage` can assign published surveys to active employees and cancel assignments that the backend still considers cancellable.
- `survey.review` can open submitted revisions, review every captured answer, approve submissions or reject them with the backend-required rejection comment.
- Survey status, assignment and review actions continue to produce the existing audit events; the Admin Web does not create a parallel workflow or bypass server-side rules.

Survey project/employee selectors use dedicated permission-gated admin lookup endpoints so survey operators do not need unrelated task-management screens merely to populate a form.

## RDP, IP and website assignments

See `docs/access-assignments.md` for data and security rules.

The Admin Web provides three dedicated sections:

- RDP Assign
- IP Assign
- Website Assign

Write buttons require `access.assignments.manage`; read access requires `access.assignments.read`.

RDP and website forms deliberately do not accept passwords, private keys, cookies or other reusable secrets. RDP may store a credential-manager reference only.
