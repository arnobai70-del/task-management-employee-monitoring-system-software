# Transparent Monitoring Telemetry

## Purpose

This module records narrowly scoped business activity needed for workforce operations while keeping collection transparent and bounded. It is not a general device-surveillance or browser-history system.

Monitoring activity is accepted only while an employee is authenticated through the Employee Desktop application. Employee identity is always resolved by the API from the authenticated user; telemetry endpoints do not accept an employee ID from the client.

## What can be recorded

### Approved work applications

Administrators create explicit application rules containing:

- a simple executable process name, such as `code` or `devenv`;
- a human-readable application name;
- active/inactive state; and
- an explicit `CaptureWindowTitle` flag.

The Employee Desktop checks the foreground process name first. If the process does not match an active approved rule, the client does not read the window title and sends no application-activity record.

For an approved application, the API stores an activity segment containing the approved process/application identity and timestamps. A window title is stored only when the matching rule explicitly enables title capture. Window titles are bounded to 300 characters.

### Approved business domains

Business website telemetry stores hostnames only, for example `jira.company.example`.

The API rejects values containing a URL scheme, path, query string, fragment, port or whitespace. It can accept a hostname when either:

- it matches an active administrator-approved business-domain rule, including an allowed subdomain when configured; or
- it is the hostname of an active website assignment belonging to that same employee.

The current Employee Desktop does not scrape browser history or inspect unrelated browser tabs. Its built-in company website action records the selected assignment's hostname and then opens the assigned URL using the operating system browser.

## Activity segments

The server coalesces consecutive observations of the same approved application/title or business hostname into a `MonitoringActivitySegment` rather than storing a high-frequency raw event stream.

A segment stores:

- employee reference;
- activity kind (`Application` or `BusinessDomain`);
- approved process/application metadata or approved hostname;
- optional explicitly permitted window title;
- first and last observation timestamps;
- configured sample interval; and
- sample count.

The Admin UI presents an approximate duration derived from the segment timestamps and sample interval.

## Policy and retention

The global monitoring policy contains:

- enabled/disabled state;
- sample interval, bounded to 15–300 seconds;
- retention period, bounded to 1–365 days; and
- employee-facing disclosure text.

Employee Desktop refreshes the policy periodically while signed in and displays the current disclosure in its Privacy tab. Disabling monitoring prevents new application/domain activity from being accepted.

Expired activity segments are purged by a server background job. Administrative activity queries are also bounded by the configured retention cutoff and a maximum 31-day explicit query window. The default Admin view requests recent activity rather than an unbounded history.

## Authorization

Organization-wide monitoring access is separate from ordinary employee, attendance, task and reporting permissions:

- `monitoring.read` — view monitoring policy, approved rules and retained approved activity;
- `monitoring.manage` — update policy/retention/disclosure and create/update/deactivate approved app/domain rules.

Employee self endpoints require authentication and an active linked employee profile. They do not grant organization-wide monitoring visibility.

Policy and rule mutations are audit logged. The policy audit event records operational settings such as enabled state, interval and retention; it does not copy the disclosure body into audit metadata.

## API surface

Administrative endpoints:

- `GET /api/monitoring/policy`
- `PUT /api/monitoring/policy`
- `GET /api/monitoring/applications`
- `POST /api/monitoring/applications`
- `PUT /api/monitoring/applications/{id}`
- `GET /api/monitoring/domains`
- `POST /api/monitoring/domains`
- `PUT /api/monitoring/domains/{id}`
- `GET /api/monitoring/activity`

Employee self endpoints:

- `GET /api/me/monitoring/policy`
- `POST /api/me/monitoring/application-activity`
- `POST /api/me/monitoring/business-domain-activity`

## Explicitly excluded collection

This module does **not** collect:

- keystrokes or typed text;
- passwords or authentication secrets;
- screenshots or screen recordings;
- camera video or microphone audio;
- full URL paths, query strings or fragments;
- page content or form content;
- browser history or unrelated browsing;
- unrelated personal files; or
- hidden/covert device activity.

Any future telemetry category must be designed separately with a demonstrated business need, clear employee disclosure, explicit permission boundaries, auditability and retention controls before implementation.
