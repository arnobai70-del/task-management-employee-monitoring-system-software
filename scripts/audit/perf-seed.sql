-- Synthetic load-test data for a SCRATCH database only (never production).
-- Usage: psql -v employees=1000 -v days=60 -d <scratch_db> -f scripts/audit/perf-seed.sql
-- All names, emails and processes are fictional; users get an unusable password hash.
\set ON_ERROR_STOP on
BEGIN;

INSERT INTO departments ("Id","Code","NormalizedCode","Name","NormalizedName","IsActive","CreatedAtUtc","UpdatedAtUtc")
SELECT gen_random_uuid(), 'PD' || d, 'PD' || d, 'Perf Department ' || d, upper('Perf Department ' || d), true, now(), now()
FROM generate_series(1, 10) d;

INSERT INTO shifts ("Id","Code","NormalizedCode","Name","NormalizedName","StartTime","EndTime","TimeZoneId","GraceMinutes","IsActive","CreatedAtUtc","UpdatedAtUtc")
VALUES (gen_random_uuid(), 'PERF-DAY', 'PERF-DAY', 'Perf Day', 'PERF DAY', '09:00', '18:00', 'Asia/Dhaka', 10, true, now(), now());

CREATE TEMP TABLE perf_emp AS
SELECT n, gen_random_uuid() AS user_id, gen_random_uuid() AS emp_id, gen_random_uuid() AS asg_id
FROM generate_series(1, :employees) n;

INSERT INTO users ("Id","Email","NormalizedEmail","PasswordHash","IsActive","FailedLoginAttempts","CreatedAtUtc","UpdatedAtUtc")
SELECT user_id, 'perf' || n || '@example.invalid', upper('perf' || n || '@example.invalid'), 'unusable-perf-hash', true, 0, now(), now()
FROM perf_emp;

INSERT INTO employees ("Id","UserId","DepartmentId","EmployeeCode","NormalizedEmployeeCode","FullName","NormalizedFullName","JobTitle","EmploymentType","JoinedOn","IsActive","CreatedAtUtc","UpdatedAtUtc")
SELECT e.emp_id, e.user_id, (SELECT "Id" FROM departments WHERE "Code" = 'PD' || (1 + e.n % 10)),
       'PERF-' || lpad(e.n::text, 5, '0'), 'PERF-' || lpad(e.n::text, 5, '0'),
       'Perf Employee ' || e.n, upper('Perf Employee ' || e.n), 'Operator', 'FullTime',
       current_date - 400, true, now(), now()
FROM perf_emp e;

INSERT INTO employee_shift_assignments ("Id","EmployeeId","ShiftId","EffectiveFrom","EffectiveTo","CreatedAtUtc")
SELECT asg_id, emp_id, (SELECT "Id" FROM shifts WHERE "Code" = 'PERF-DAY'), current_date - 400, NULL, now()
FROM perf_emp;

-- One closed session per employee per weekday; 03:00 UTC = 09:00 Asia/Dhaka.
CREATE TEMP TABLE perf_sessions AS
SELECT gen_random_uuid() AS id, e.emp_id, e.asg_id, d::date AS work_date,
       (d::date + time '03:00') AT TIME ZONE 'UTC' AS sched_start,
       (d::date + time '12:00') AT TIME ZONE 'UTC' AS sched_end,
       (e.n * 7 + extract(doy FROM d)::int) % 25 AS late
FROM perf_emp e
CROSS JOIN generate_series(current_date - :days, current_date - 1, interval '1 day') d
WHERE extract(isodow FROM d) < 6;

INSERT INTO work_sessions ("Id","EmployeeId","ShiftId","ShiftAssignmentId","WorkDate","ScheduledStartUtc","ScheduledEndUtc","StartedAtUtc","EndedAtUtc","LateMinutes","EarlyLeaveMinutes","TotalBreakMinutes","CreatedAtUtc","UpdatedAtUtc","ClosureSource")
SELECT s.id, s.emp_id, (SELECT "Id" FROM shifts WHERE "Code" = 'PERF-DAY'), s.asg_id, s.work_date, s.sched_start, s.sched_end,
       s.sched_start + make_interval(mins => s.late), s.sched_end, greatest(s.late - 10, 0), 0, 45, s.sched_start, s.sched_end, 'Employee'
FROM perf_sessions s;

INSERT INTO work_breaks ("Id","WorkSessionId","StartedAtUtc","EndedAtUtc","DurationMinutes","CreatedAtUtc")
SELECT gen_random_uuid(), s.id, s.sched_start + interval '4 hours', s.sched_start + interval '4 hours 45 minutes', 45, s.sched_start
FROM perf_sessions s;

INSERT INTO projects ("Id","Code","NormalizedCode","Name","NormalizedName","Status","StartDate","CreatedAtUtc","UpdatedAtUtc")
SELECT gen_random_uuid(), 'PP' || p, 'PP' || p, 'Perf Project ' || p, upper('Perf Project ' || p), 'Active', current_date - 400, now(), now()
FROM generate_series(1, 50) p;

INSERT INTO project_members ("Id","ProjectId","EmployeeId","Role","IsActive","AddedAtUtc")
SELECT gen_random_uuid(), (SELECT "Id" FROM projects WHERE "Code" = 'PP' || (1 + e.n % 50)), e.emp_id, 'Member', true, now()
FROM perf_emp e;

INSERT INTO project_tasks ("Id","ProjectId","Title","NormalizedTitle","Status","Priority","AssigneeEmployeeId","DueDate","CompletedAtUtc","CreatedAtUtc","UpdatedAtUtc")
SELECT gen_random_uuid(), (SELECT "Id" FROM projects WHERE "Code" = 'PP' || (1 + e.n % 50)),
       'Perf task ' || e.n || '-' || t, upper('Perf task ' || e.n || '-' || t),
       (ARRAY['ToDo','InProgress','Done','Done','Blocked'])[1 + (e.n + t) % 5],
       (ARRAY['Low','Normal','High','Urgent'])[1 + (e.n * t) % 4],
       e.emp_id, current_date + ((e.n + t) % 30) - 10,
       CASE WHEN (e.n + t) % 5 IN (2, 3) THEN now() - make_interval(days => (e.n + t) % 60) END,
       now() - make_interval(days => (e.n + t) % 90), now()
FROM perf_emp e CROSS JOIN generate_series(1, 20) t;

INSERT INTO employee_presences ("EmployeeId","LastSeenAtUtc","ClientKind","ClientVersion","CreatedAtUtc","UpdatedAtUtc")
SELECT emp_id, now() - make_interval(secs => n % 600), 'desktop', '1.0.0', now(), now() FROM perf_emp;

-- Business-scoped activity segments: application names only, no window titles.
INSERT INTO monitoring_activity_segments ("Id","EmployeeId","Kind","ProcessName","ApplicationName","StartedAtUtc","LastObservedAtUtc","SampleIntervalSeconds","SampleCount","CreatedAtUtc","UpdatedAtUtc")
SELECT gen_random_uuid(), s.emp_id, 'Application', 'perfapp' || k || '.exe', 'Perf App ' || k,
       s.sched_start + make_interval(mins => k * 50), s.sched_start + make_interval(mins => k * 50 + 45), 30, 90,
       s.sched_start, s.sched_start
FROM perf_sessions s CROSS JOIN generate_series(1, 8) k;

INSERT INTO audit_logs ("Id","ActorUserId","Action","TargetType","TargetId","MetadataJson","IpAddress","UserAgent","CreatedAtUtc")
SELECT gen_random_uuid(), e.user_id,
       (ARRAY['auth.login.succeeded','attendance.checked_in','attendance.checked_out','task.updated','presence.heartbeat'])[1 + (e.n + a) % 5],
       'Perf', e.emp_id::text, '{}', '10.0.' || (e.n % 250) || '.' || (a % 250), 'perf-seed',
       now() - make_interval(mins => (e.n * 13 + a * 37) % (60 * 24 * :days))
FROM perf_emp e CROSS JOIN generate_series(1, 300) a;

COMMIT;
ANALYZE departments, shifts, users, employees, employee_shift_assignments, work_sessions, work_breaks, projects, project_members, project_tasks, employee_presences, monitoring_activity_segments, audit_logs;
