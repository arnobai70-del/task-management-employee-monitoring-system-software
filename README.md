# Task Management & Employee Monitoring System

A centralized, internet-required employee task management and monitoring platform for office teams such as Web Development and Survey/Field Operations.

## Core Architecture

The system will use a client-server model:

- **Employee Desktop App** installed on every employee PC
- **Background Windows Agent/Service** for presence, work-session, and approved activity telemetry
- **Central Backend API** hosted on a server/VPS
- **Central Database** for employees, tasks, attendance, monitoring events, survey work, reports, and audit logs
- **Admin/Manager Dashboard** for monitoring, task assignment, reports, and team management

> The employee desktop software requires an internet connection and will not operate as an offline-first application.

## Planned Modules

### 1. Authentication & Access Control
- Login/logout
- Role-based permissions
- Admin, HR, Manager, Team Lead, Developer, Survey Supervisor, Surveyor roles
- Session/device tracking

### 2. Employee Management
- Employee profiles
- Departments and designations
- Teams
- Employment status
- Role and permission assignment

### 3. Task & Project Management
- Projects, tasks, and subtasks
- Assignment, priority, deadline, status
- Comments and attachments
- Task timer and work logs
- Review/approval workflow

### 4. Developer Team Workflow
- Project/module/task tracking
- Bug/issue tracking
- Task progress and review status
- Estimated vs actual work time
- Delivery and quality-oriented performance metrics

### 5. Survey Team Workflow
- Survey campaigns/projects
- Area/territory assignments
- Target tracking
- Survey submission progress
- Supervisor verification/rejection
- Field check-in/out and location features where appropriate and transparently disclosed

### 6. Attendance & Time Tracking
- Check-in/check-out
- Working hours
- Late/absence/early-leave tracking
- Daily work sessions
- Active/idle status where appropriate

### 7. Desktop Monitoring Agent
- Online/offline state
- Periodic server heartbeat
- Work-session state
- Active/idle time
- Approved application-usage telemetry where business-required

Intrusive monitoring such as keystroke logging, continuous screenshots, camera access, or continuous location tracking should not be enabled by default. Any sensitive monitoring must be transparent, access-controlled, justified by a business need, and subject to an explicit workplace policy and applicable law.

### 8. Admin Dashboard & Reports
- Employee online/offline overview
- Team-wise status
- Project/task progress
- Attendance summary
- Survey progress
- Developer progress
- Overdue tasks
- Productivity/performance reports
- Audit logs

### 9. Notifications
- Task assignment
- Deadline reminders
- Overdue alerts
- Approval/rejection updates
- Manager comments
- Attendance events

## Proposed Technology Stack

For a Windows-first deployment:

- **Desktop App:** C# / .NET (WPF or WinUI)
- **Background Agent:** .NET Windows Service
- **Backend API:** ASP.NET Core
- **Database:** PostgreSQL
- **Admin Dashboard:** React + TypeScript
- **Realtime Communication:** SignalR / WebSocket
- **Deployment:** VPS/Cloud server with HTTPS

The exact stack may evolve as implementation requirements become clearer.

## High-Level Data Model

Planned entities include:

- users
- roles
- permissions
- employees
- departments
- teams
- devices
- sessions
- projects
- tasks
- subtasks
- task_updates
- task_comments
- work_logs
- attendance
- timesheets
- survey_projects
- survey_assignments
- survey_submissions
- location_events
- activity_events
- notifications
- audit_logs
- performance_metrics

## Development Workflow

All implementation work for this project will be committed to this repository with clear commit messages. Significant features should be developed in focused branches and merged after review/testing where practical.

## Repository

`arnobai70-del/task-management-employee-monitoring-system-software`
