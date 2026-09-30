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

## Survey website assignments

Surveys are **externally hosted work**. Administrators do not build questionnaires in this system, and employees do not enter survey answers into the Task Monitoring application.

The boss/supervisor supplies the exact HTTP/HTTPS URL where the survey already exists and assigns that link to an employee. An assignment can include:

- employee;
- assignment title;
- external survey website URL;
- optional start date;
- optional due date;
- instructions;
- active/inactive state.

`surveys.read` can view these assignments and `surveys.manage` can create, edit, schedule or deactivate them. The backend validates that active assignments target active employee accounts, accepts only absolute HTTP/HTTPS URLs without embedded credentials, rejects invalid date ranges, and prevents duplicate active assignments for the same employee and URL.

In the Windows Employee Workspace, the assigned survey is presented as company-provided web work. The employee selects it and opens the URL in the default browser, then completes the survey on the external site. The Task Monitoring system does **not** collect the external form fields, submitted answers, passwords, cookies or page contents.

When the dedicated survey-link open endpoint is used, the audit trail records that the assigned link was opened and stores only operational metadata such as the assignment and hostname; it does not store the external URL path or query string.

The earlier internal questionnaire/submission model remains database-compatible for existing installations, but it is not the Admin Web workflow for new survey work.

## RDP, IP and website assignments

See `docs/access-assignments.md` for data and security rules.

The Admin Web provides three dedicated sections:

- RDP Assign
- IP Assign
- Website Assign

Write buttons require `access.assignments.manage`; read access requires `access.assignments.read`.

RDP and website forms deliberately do not accept passwords, private keys, cookies or other reusable secrets. RDP may store a credential-manager reference only.
