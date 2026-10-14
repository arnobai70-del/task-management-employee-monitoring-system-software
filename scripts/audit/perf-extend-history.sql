-- Extends a SCRATCH database already seeded by perf-seed.sql (never production) for large-range checks.
-- Usage: psql -v days=60 -v history=365 -v extra=4000 -d <scratch_db> -f scripts/audit/perf-extend-history.sql
--   days    = the value perf-seed.sql was run with (its sessions cover the last :days days)
--   history = total days of attendance history wanted
--   extra   = additional fictional employees, with presence rows and full history
-- All names and emails are fictional; users get an unusable password hash.
\set ON_ERROR_STOP on
BEGIN;

CREATE TEMP TABLE perf_new AS
SELECT n, gen_random_uuid() AS user_id, gen_random_uuid() AS emp_id, gen_random_uuid() AS asg_id
FROM generate_series(100001, 100000 + :extra) n;

INSERT INTO users ("Id","Email","NormalizedEmail","PasswordHash","IsActive","FailedLoginAttempts","CreatedAtUtc","UpdatedAtUtc")
SELECT user_id, 'perf' || n || '@example.invalid', upper('perf' || n || '@example.invalid'), 'unusable-perf-hash', true, 0, now(), now()
FROM perf_new;

INSERT INTO employees ("Id","UserId","DepartmentId","EmployeeCode","NormalizedEmployeeCode","FullName","NormalizedFullName","JobTitle","EmploymentType","JoinedOn","IsActive","CreatedAtUtc","UpdatedAtUtc")
SELECT e.emp_id, e.user_id, (SELECT "Id" FROM departments WHERE "Code" = 'PD' || (1 + e.n % 10)),
       'PERF-' || e.n, 'PERF-' || e.n, 'Perf Employee ' || e.n, upper('Perf Employee ' || e.n), 'Operator', 'FullTime',
       current_date - 400, true, now(), now()
FROM perf_new e;

INSERT INTO employee_shift_assignments ("Id","EmployeeId","ShiftId","EffectiveFrom","EffectiveTo","CreatedAtUtc")
SELECT asg_id, emp_id, (SELECT "Id" FROM shifts WHERE "Code" = 'PERF-DAY'), current_date - 400, NULL, now()
FROM perf_new;

INSERT INTO employee_presences ("EmployeeId","LastSeenAtUtc","ClientKind","ClientVersion","CreatedAtUtc","UpdatedAtUtc")
SELECT emp_id, now() - make_interval(secs => n % 600), 'desktop', '1.0.0', now(), now() FROM perf_new;

-- Older history for the original employees, full history for the new ones; weekdays only, as perf-seed.sql.
CREATE TEMP TABLE perf_hist AS
SELECT gen_random_uuid() AS id, a."EmployeeId" AS emp_id, a."Id" AS asg_id, d::date AS work_date,
       (d::date + time '03:00') AT TIME ZONE 'UTC' AS sched_start,
       (d::date + time '12:00') AT TIME ZONE 'UTC' AS sched_end,
       (abs(hashtext(a."EmployeeId"::text)) + extract(doy FROM d)::int) % 25 AS late
FROM employee_shift_assignments a
JOIN employees e ON e."Id" = a."EmployeeId" AND e."EmployeeCode" LIKE 'PERF-%'
CROSS JOIN generate_series(current_date - :history, current_date - 1, interval '1 day') d
WHERE extract(isodow FROM d) < 6
  AND (d::date < current_date - :days OR a."EmployeeId" IN (SELECT emp_id FROM perf_new));

INSERT INTO work_sessions ("Id","EmployeeId","ShiftId","ShiftAssignmentId","WorkDate","ScheduledStartUtc","ScheduledEndUtc","StartedAtUtc","EndedAtUtc","LateMinutes","EarlyLeaveMinutes","TotalBreakMinutes","CreatedAtUtc","UpdatedAtUtc","ClosureSource")
SELECT s.id, s.emp_id, (SELECT "Id" FROM shifts WHERE "Code" = 'PERF-DAY'), s.asg_id, s.work_date, s.sched_start, s.sched_end,
       s.sched_start + make_interval(mins => s.late), s.sched_end, greatest(s.late - 10, 0), 0, 45, s.sched_start, s.sched_end, 'Employee'
FROM perf_hist s;

INSERT INTO work_breaks ("Id","WorkSessionId","StartedAtUtc","EndedAtUtc","DurationMinutes","CreatedAtUtc")
SELECT gen_random_uuid(), s.id, s.sched_start + interval '4 hours', s.sched_start + interval '4 hours 45 minutes', 45, s.sched_start
FROM perf_hist s;

COMMIT;
ANALYZE users, employees, employee_shift_assignments, employee_presences, work_sessions, work_breaks;
