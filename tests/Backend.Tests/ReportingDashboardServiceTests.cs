using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class ReportingDashboardServiceTests
{
    [Fact]
    public async Task Invalid_or_oversized_date_ranges_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var service = Service(db);

        var reversed = await service.GetDashboardAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 29), cancellationToken);
        var oversized = await service.GetDashboardAsync(new DateOnly(2025, 9, 28), new DateOnly(2026, 9, 29), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, reversed.Status);
        Assert.Equal("report_date_range_invalid", reversed.ErrorCode);
        Assert.Equal(OperationStatus.Invalid, oversized.Status);
        Assert.Equal("report_date_range_too_large", oversized.ErrorCode);
    }

    [Fact]
    public async Task Dashboard_aggregates_workforce_attendance_tasks_projects_and_surveys()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var department = AddDepartment(db, "OPS", "Operations");
        var employee = AddEmployee(db, department, true, "EMP-001", "Active Employee");
        AddEmployee(db, department, false, "EMP-002", "Inactive Employee");
        AddWorkSession(db, employee, new DateOnly(2026, 9, 28), ended: true, lateMinutes: 10, earlyLeaveMinutes: 5, breakMinutes: 15);
        AddWorkSession(db, employee, new DateOnly(2026, 9, 29), ended: false, lateMinutes: 0, earlyLeaveMinutes: null, breakMinutes: 0);

        var activeProject = AddProject(db, "PRJ-A", "Active Project", ProjectStatus.Active, new DateOnly(2026, 9, 28));
        AddProject(db, "PRJ-C", "Completed Project", ProjectStatus.Completed, new DateOnly(2026, 9, 20));
        AddTask(db, activeProject, null, ProjectTaskStatus.ToDo, ProjectTaskPriority.High, new DateOnly(2026, 9, 28));
        AddTask(db, activeProject, employee, ProjectTaskStatus.Done, ProjectTaskPriority.Normal, new DateOnly(2026, 9, 28), Utc(2026, 9, 29, 1));
        AddTask(db, activeProject, employee, ProjectTaskStatus.Cancelled, ProjectTaskPriority.Low, null);

        var form = AddSurveyForm(db, activeProject, "SURV-A", "Field Survey", SurveyFormStatus.Published);
        var pending = AddSurveyAssignment(db, form, employee, SurveyAssignmentStatus.Submitted, new DateOnly(2026, 9, 28));
        var approved = AddSurveyAssignment(db, form, employee, SurveyAssignmentStatus.Approved, new DateOnly(2026, 9, 30));
        AddSubmission(db, pending, SurveySubmissionStatus.Submitted, Utc(2026, 9, 29, 2), null);
        AddSubmission(db, approved, SurveySubmissionStatus.Approved, Utc(2026, 9, 29, 3), Utc(2026, 9, 29, 4));
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).GetDashboardAsync(new DateOnly(2026, 9, 28), new DateOnly(2026, 9, 29), cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var dashboard = result.Value!;
        Assert.Equal(1, dashboard.Workforce.ActiveEmployees);
        Assert.Equal(1, dashboard.Workforce.ActiveDepartments);
        Assert.Equal(2, dashboard.Attendance.Sessions);
        Assert.Equal(1, dashboard.Attendance.DistinctEmployees);
        Assert.Equal(1, dashboard.Attendance.CompletedSessions);
        Assert.Equal(1, dashboard.Attendance.OpenSessions);
        Assert.Equal(1, dashboard.Attendance.LateSessions);
        Assert.Equal(1, dashboard.Attendance.EarlyLeaveSessions);
        Assert.Equal(15, dashboard.Attendance.TotalBreakMinutes);
        Assert.Equal(1, dashboard.Projects.Active);
        Assert.Equal(1, dashboard.Projects.Completed);
        Assert.Equal(1, dashboard.Projects.Overdue);
        Assert.Equal(1, dashboard.Tasks.Open);
        Assert.Equal(1, dashboard.Tasks.ToDo);
        Assert.Equal(1, dashboard.Tasks.Done);
        Assert.Equal(1, dashboard.Tasks.Cancelled);
        Assert.Equal(1, dashboard.Tasks.Overdue);
        Assert.Equal(1, dashboard.Tasks.UnassignedOpen);
        Assert.Equal(1, dashboard.Tasks.CompletedInPeriod);
        Assert.Equal(1, dashboard.Surveys.PublishedForms);
        Assert.Equal(1, dashboard.Surveys.ActiveAssignments);
        Assert.Equal(1, dashboard.Surveys.PendingReview);
        Assert.Equal(1, dashboard.Surveys.ApprovedAssignments);
        Assert.Equal(1, dashboard.Surveys.OverdueAssignments);
        Assert.Equal(2, dashboard.Surveys.SubmittedInPeriod);
        Assert.Equal(1, dashboard.Surveys.ReviewedInPeriod);
    }

    [Fact]
    public async Task Attendance_daily_report_filters_department_and_fills_empty_dates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var operations = AddDepartment(db, "OPS", "Operations");
        var engineering = AddDepartment(db, "ENG", "Engineering");
        var operationsEmployee = AddEmployee(db, operations, true, "OPS-001", "Operations Employee");
        var engineeringEmployee = AddEmployee(db, engineering, true, "ENG-001", "Engineering Employee");
        AddWorkSession(db, operationsEmployee, new DateOnly(2026, 9, 28), ended: true, lateMinutes: 4, earlyLeaveMinutes: null, breakMinutes: 10);
        AddWorkSession(db, engineeringEmployee, new DateOnly(2026, 9, 29), ended: true, lateMinutes: 0, earlyLeaveMinutes: null, breakMinutes: 5);
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).GetAttendanceDailyAsync(
            new DateOnly(2026, 9, 28),
            new DateOnly(2026, 9, 30),
            operations.Id,
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var rows = result.Value!.ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(1, rows[0].Sessions);
        Assert.Equal(1, rows[0].LateSessions);
        Assert.Equal(10, rows[0].TotalBreakMinutes);
        Assert.Equal(0, rows[1].Sessions);
        Assert.Equal(0, rows[2].Sessions);
    }

    [Fact]
    public async Task Project_progress_calculates_open_overdue_and_completion_metrics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var department = AddDepartment(db, "DEV", "Development");
        var employee = AddEmployee(db, department, true, "DEV-001", "Developer");
        var project = AddProject(db, "PRG", "Progress Project", ProjectStatus.Active, new DateOnly(2026, 10, 31));
        db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, Project = project, EmployeeId = employee.Id, Employee = employee, IsActive = true });
        AddTask(db, project, employee, ProjectTaskStatus.Done, ProjectTaskPriority.Normal, new DateOnly(2026, 9, 20), Utc(2026, 9, 25, 12));
        AddTask(db, project, employee, ProjectTaskStatus.Blocked, ProjectTaskPriority.High, new DateOnly(2026, 9, 28));
        AddTask(db, project, employee, ProjectTaskStatus.ToDo, ProjectTaskPriority.Normal, new DateOnly(2026, 10, 10));
        AddTask(db, project, employee, ProjectTaskStatus.Cancelled, ProjectTaskPriority.Low, new DateOnly(2026, 9, 15));
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).GetProjectProgressAsync(project.Id, cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var metric = Assert.Single(result.Value!);
        Assert.Equal(1, metric.ActiveMembers);
        Assert.Equal(4, metric.TotalTasks);
        Assert.Equal(2, metric.OpenTasks);
        Assert.Equal(1, metric.DoneTasks);
        Assert.Equal(1, metric.BlockedTasks);
        Assert.Equal(1, metric.CancelledTasks);
        Assert.Equal(1, metric.OverdueTasks);
        Assert.Equal(33.33m, metric.CompletionPercent);
    }

    [Fact]
    public async Task Employee_workload_combines_open_tasks_and_active_survey_assignments()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var department = AddDepartment(db, "FIELD", "Field Team");
        var busy = AddEmployee(db, department, true, "FLD-001", "Busy Employee");
        var light = AddEmployee(db, department, true, "FLD-002", "Light Employee");
        var project = AddProject(db, "OPS-W", "Workload Project", ProjectStatus.Active, new DateOnly(2026, 10, 31));
        AddTask(db, project, busy, ProjectTaskStatus.InProgress, ProjectTaskPriority.Urgent, new DateOnly(2026, 9, 28));
        AddTask(db, project, busy, ProjectTaskStatus.ToDo, ProjectTaskPriority.Normal, new DateOnly(2026, 10, 5));
        AddTask(db, project, light, ProjectTaskStatus.ToDo, ProjectTaskPriority.Normal, new DateOnly(2026, 10, 5));
        var form = AddSurveyForm(db, project, "LOAD", "Workload Survey", SurveyFormStatus.Published);
        AddSurveyAssignment(db, form, busy, SurveyAssignmentStatus.InProgress, new DateOnly(2026, 9, 28));
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).GetEmployeeWorkloadAsync(department.Id, 10, cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var rows = result.Value!.ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(busy.Id, rows[0].EmployeeId);
        Assert.Equal(2, rows[0].OpenTasks);
        Assert.Equal(1, rows[0].UrgentOpenTasks);
        Assert.Equal(1, rows[0].OverdueTasks);
        Assert.Equal(1, rows[0].ActiveSurveyAssignments);
        Assert.Equal(1, rows[0].OverdueSurveyAssignments);
        Assert.Equal(3, rows[0].TotalOpenItems);
        Assert.Equal(1, rows[1].TotalOpenItems);
    }

    [Fact]
    public async Task Survey_progress_reports_assignment_states_overdue_and_approval_percentage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var department = AddDepartment(db, "SURV", "Survey Team");
        var employee = AddEmployee(db, department, true, "SUR-001", "Survey Employee");
        var project = AddProject(db, "SUR-P", "Survey Project", ProjectStatus.Active, new DateOnly(2026, 10, 31));
        var form = AddSurveyForm(db, project, "HOUSE", "Household Survey", SurveyFormStatus.Published);
        AddSurveyAssignment(db, form, employee, SurveyAssignmentStatus.Assigned, new DateOnly(2026, 9, 28));
        AddSurveyAssignment(db, form, employee, SurveyAssignmentStatus.Submitted, new DateOnly(2026, 10, 1));
        AddSurveyAssignment(db, form, employee, SurveyAssignmentStatus.Approved, new DateOnly(2026, 10, 1));
        AddSurveyAssignment(db, form, employee, SurveyAssignmentStatus.Rejected, new DateOnly(2026, 10, 1));
        AddSurveyAssignment(db, form, employee, SurveyAssignmentStatus.Cancelled, new DateOnly(2026, 9, 20));
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).GetSurveyProgressAsync(project.Id, cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var metric = Assert.Single(result.Value!);
        Assert.Equal(5, metric.TotalAssignments);
        Assert.Equal(1, metric.Assigned);
        Assert.Equal(0, metric.InProgress);
        Assert.Equal(1, metric.PendingReview);
        Assert.Equal(1, metric.Approved);
        Assert.Equal(1, metric.Rejected);
        Assert.Equal(1, metric.Cancelled);
        Assert.Equal(1, metric.Overdue);
        Assert.Equal(25m, metric.ApprovalPercent);
    }

    private static AppDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ReportingDashboardService Service(AppDbContext db) =>
        new(db, new FixedTimeProvider(new DateTimeOffset(Utc(2026, 9, 29, 6))));

    private static Department AddDepartment(AppDbContext db, string code, string name)
    {
        var department = new Department
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            IsActive = true
        };
        db.Departments.Add(department);
        return department;
    }

    private static Employee AddEmployee(AppDbContext db, Department department, bool isActive, string employeeCode, string fullName)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"{Guid.NewGuid():N}@EXAMPLE.COM",
            PasswordHash = "test-hash",
            IsActive = isActive
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            DepartmentId = department.Id,
            Department = department,
            EmployeeCode = employeeCode,
            NormalizedEmployeeCode = employeeCode.ToUpperInvariant(),
            FullName = fullName,
            NormalizedFullName = fullName.ToUpperInvariant(),
            JobTitle = "Tester",
            IsActive = isActive
        };
        db.Users.Add(user);
        db.Employees.Add(employee);
        return employee;
    }

    private static void AddWorkSession(
        AppDbContext db,
        Employee employee,
        DateOnly workDate,
        bool ended,
        int lateMinutes,
        int? earlyLeaveMinutes,
        int breakMinutes)
    {
        var shift = new Shift
        {
            Code = $"S-{Guid.NewGuid():N}"[..12],
            NormalizedCode = Guid.NewGuid().ToString("N"),
            Name = $"Shift {Guid.NewGuid():N}"[..18],
            NormalizedName = Guid.NewGuid().ToString("N"),
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            TimeZoneId = "UTC"
        };
        var assignment = new EmployeeShiftAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            EffectiveFrom = workDate
        };
        var started = Utc(workDate.Year, workDate.Month, workDate.Day, 9);
        db.Shifts.Add(shift);
        db.EmployeeShiftAssignments.Add(assignment);
        db.WorkSessions.Add(new WorkSession
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            ShiftAssignmentId = assignment.Id,
            ShiftAssignment = assignment,
            WorkDate = workDate,
            ScheduledStartUtc = started,
            ScheduledEndUtc = started.AddHours(8),
            StartedAtUtc = started.AddMinutes(lateMinutes),
            EndedAtUtc = ended ? started.AddHours(8).AddMinutes(-(earlyLeaveMinutes ?? 0)) : null,
            LateMinutes = lateMinutes,
            EarlyLeaveMinutes = earlyLeaveMinutes,
            TotalBreakMinutes = breakMinutes
        });
    }

    private static Project AddProject(AppDbContext db, string code, string name, ProjectStatus status, DateOnly? dueDate)
    {
        var project = new Project
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Status = status,
            DueDate = dueDate
        };
        db.Projects.Add(project);
        return project;
    }

    private static void AddTask(
        AppDbContext db,
        Project project,
        Employee? employee,
        ProjectTaskStatus status,
        ProjectTaskPriority priority,
        DateOnly? dueDate,
        DateTime? completedAtUtc = null)
    {
        var title = $"Task {Guid.NewGuid():N}"[..18];
        db.ProjectTasks.Add(new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = title,
            NormalizedTitle = title.ToUpperInvariant(),
            Status = status,
            Priority = priority,
            AssigneeEmployeeId = employee?.Id,
            AssigneeEmployee = employee,
            DueDate = dueDate,
            CompletedAtUtc = completedAtUtc
        });
    }

    private static SurveyForm AddSurveyForm(AppDbContext db, Project project, string code, string name, SurveyFormStatus status)
    {
        var form = new SurveyForm
        {
            ProjectId = project.Id,
            Project = project,
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            Status = status
        };
        db.SurveyForms.Add(form);
        return form;
    }

    private static SurveyAssignment AddSurveyAssignment(
        AppDbContext db,
        SurveyForm form,
        Employee employee,
        SurveyAssignmentStatus status,
        DateOnly? dueDate)
    {
        var assignment = new SurveyAssignment
        {
            SurveyFormId = form.Id,
            SurveyForm = form,
            EmployeeId = employee.Id,
            Employee = employee,
            Status = status,
            DueDate = dueDate
        };
        db.SurveyAssignments.Add(assignment);
        return assignment;
    }

    private static void AddSubmission(
        AppDbContext db,
        SurveyAssignment assignment,
        SurveySubmissionStatus status,
        DateTime? submittedAtUtc,
        DateTime? reviewedAtUtc)
    {
        db.SurveySubmissions.Add(new SurveySubmission
        {
            SurveyAssignmentId = assignment.Id,
            SurveyAssignment = assignment,
            RevisionNumber = 1,
            Status = status,
            SubmittedAtUtc = submittedAtUtc,
            ReviewedAtUtc = reviewedAtUtc
        });
    }

    private static DateTime Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
