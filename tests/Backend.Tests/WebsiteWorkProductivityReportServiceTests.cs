using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class WebsiteWorkProductivityReportServiceTests
{
    [Fact]
    public async Task Report_counts_review_lifecycle_and_working_intervals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-PROD-1", "Jihad");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, new DateTime(2026, 9, 30, 9, 50, 0, DateTimeKind.Utc), new DateOnly(2026, 10, 1));

        AddActivity(task, WebsiteWorkService.ConfiguredAction, task.CreatedAtUtc);
        AddActivity(task, WebsiteWorkService.StartedAction, new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.SubmittedAction, new DateTime(2026, 9, 30, 10, 30, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.ReopenedAction, new DateTime(2026, 9, 30, 10, 40, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.SubmittedAction, new DateTime(2026, 9, 30, 10, 55, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.ApprovedAction, new DateTime(2026, 9, 30, 11, 5, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkService.CompletedAction, new DateTime(2026, 9, 30, 11, 5, 0, DateTimeKind.Utc));
        task.Status = ProjectTaskStatus.Done;
        task.CompletedAtUtc = new DateTime(2026, 9, 30, 11, 5, 0, DateTimeKind.Utc);
        task.UpdatedAtUtc = task.CompletedAtUtc.Value;
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkProductivityReportService(db, new FixedTimeProvider(now));
        var result = await service.GetAsync(
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30),
            0,
            WebsiteWorkProductivityGrouping.Day,
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var report = Assert.IsType<WebsiteWorkProductivityReportResponse>(result.Value);
        Assert.Equal(1, report.Summary.Assigned);
        Assert.Equal(1, report.Summary.Started);
        Assert.Equal(45 * 60, report.Summary.WorkingSeconds);
        Assert.Equal(2, report.Summary.Submitted);
        Assert.Equal(1, report.Summary.Approved);
        Assert.Equal(1, report.Summary.Reopened);
        Assert.Equal(0, report.Summary.Overdue);
        Assert.Equal(100m, report.Summary.CompletionPercent);

        var employeeRow = Assert.Single(report.Employees);
        Assert.Equal(employee.Id, employeeRow.EmployeeId);
        Assert.Equal(report.Summary, employeeRow.Metrics);

        var period = Assert.Single(report.Periods);
        Assert.Equal(new DateOnly(2026, 9, 30), period.From);
        Assert.Equal(report.Summary, period.Metrics);
    }

    [Fact]
    public async Task Report_uses_requested_timezone_for_day_boundaries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-PROD-2", "Night Worker");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, new DateTime(2026, 9, 30, 17, 50, 0, DateTimeKind.Utc), null);

        AddActivity(task, WebsiteWorkService.ConfiguredAction, task.CreatedAtUtc);
        AddActivity(task, WebsiteWorkService.StartedAction, new DateTime(2026, 9, 30, 18, 10, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.SubmittedAction, new DateTime(2026, 9, 30, 18, 20, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkService.CompletedAction, new DateTime(2026, 9, 30, 18, 30, 0, DateTimeKind.Utc));
        task.Status = ProjectTaskStatus.Done;
        task.CompletedAtUtc = new DateTime(2026, 9, 30, 18, 30, 0, DateTimeKind.Utc);
        task.UpdatedAtUtc = task.CompletedAtUtc.Value;
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkProductivityReportService(db, new FixedTimeProvider(now));
        var dhakaDay = await service.GetAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1),
            360,
            WebsiteWorkProductivityGrouping.Day,
            cancellationToken);
        var utcDay = await service.GetAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1),
            0,
            WebsiteWorkProductivityGrouping.Day,
            cancellationToken);

        Assert.Equal(OperationStatus.Success, dhakaDay.Status);
        Assert.Equal(1, dhakaDay.Value!.Summary.Started);
        Assert.Equal(1, dhakaDay.Value.Summary.Submitted);
        Assert.Equal(1, dhakaDay.Value.Summary.Approved);
        Assert.Equal(10 * 60, dhakaDay.Value.Summary.WorkingSeconds);

        Assert.Equal(OperationStatus.Success, utcDay.Status);
        Assert.Equal(0, utcDay.Value!.Summary.Started);
        Assert.Equal(0, utcDay.Value.Summary.Submitted);
        Assert.Equal(0, utcDay.Value.Summary.Approved);
    }

    [Fact]
    public async Task Report_rejects_future_and_oversized_ranges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var service = new WebsiteWorkProductivityReportService(db, new FixedTimeProvider(now));

        var future = await service.GetAsync(
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 10, 1),
            0,
            WebsiteWorkProductivityGrouping.Day,
            cancellationToken);
        Assert.Equal(OperationStatus.Invalid, future.Status);
        Assert.Equal("report_range_future", future.ErrorCode);

        var oversized = await service.GetAsync(
            new DateOnly(2025, 9, 29),
            new DateOnly(2026, 9, 30),
            0,
            WebsiteWorkProductivityGrouping.Week,
            cancellationToken);
        Assert.Equal(OperationStatus.Invalid, oversized.Status);
        Assert.Equal("report_range_too_large", oversized.ErrorCode);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"website-work-productivity-{Guid.NewGuid():N}")
            .Options);

    private static Employee AddEmployee(AppDbContext db, string code, string name)
    {
        var department = new Department
        {
            Code = $"D-{code}",
            NormalizedCode = $"D-{code}".ToUpperInvariant(),
            Name = "Survey Team",
            NormalizedName = "SURVEY TEAM",
            IsActive = true
        };
        var user = new User
        {
            Email = $"{code.ToLowerInvariant()}@example.com",
            NormalizedEmail = $"{code.ToUpperInvariant()}@EXAMPLE.COM",
            PasswordHash = "hash",
            IsActive = true
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            DepartmentId = department.Id,
            Department = department,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = name,
            NormalizedFullName = name.ToUpperInvariant(),
            JobTitle = "Worker",
            IsActive = true
        };
        db.Departments.Add(department);
        db.Users.Add(user);
        db.Employees.Add(employee);
        return employee;
    }

    private static Project AddProject(AppDbContext db)
    {
        var project = new Project
        {
            Code = $"PROD-{Guid.NewGuid():N}"[..12],
            NormalizedCode = $"PROD-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Website Productivity",
            NormalizedName = "WEBSITE PRODUCTIVITY",
            Status = ProjectStatus.Active
        };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectTask AddWebsiteWork(
        AppDbContext db,
        Employee employee,
        Project project,
        DateTime createdAtUtc,
        DateOnly? dueDate)
    {
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = "InboxDollars - complete $5 target",
            NormalizedTitle = "INBOXDOLLARS - COMPLETE $5 TARGET",
            Status = ProjectTaskStatus.InProgress,
            Priority = ProjectTaskPriority.Normal,
            AssigneeEmployeeId = employee.Id,
            AssigneeEmployee = employee,
            DueDate = dueDate,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
        db.ProjectTasks.Add(task);
        return task;
    }

    private static void AddActivity(ProjectTask task, string action, DateTime atUtc)
    {
        task.Activities.Add(new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            ActorUserId = task.AssigneeEmployee?.UserId,
            Action = action,
            DetailsJson = "{}",
            CreatedAtUtc = atUtc
        });
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
