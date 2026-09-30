using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class WebsiteWorkTimelineServiceTests
{
    [Fact]
    public async Task Timeline_returns_target_lifecycle_comments_actors_and_working_time()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-TIME-1", "Jihad", "jihad@example.com");
        var manager = AddUser(db, "boss@example.com");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, manager.Id, new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc));

        AddActivity(task, WebsiteWorkService.ConfiguredAction, task.CreatedAtUtc, employee.UserId);
        AddActivity(task, WebsiteWorkService.StartedAction, new DateTime(2026, 9, 30, 9, 10, 0, DateTimeKind.Utc), employee.UserId);
        AddActivity(task, WebsiteWorkReviewService.SubmittedAction, new DateTime(2026, 9, 30, 9, 40, 0, DateTimeKind.Utc), employee.UserId);
        AddActivity(task, WebsiteWorkReviewService.ReopenedAction, new DateTime(2026, 9, 30, 9, 50, 0, DateTimeKind.Utc), manager.Id, "{\"comment\":\"Please correct the final amount.\"}");
        AddActivity(task, WebsiteWorkReviewService.SubmittedAction, new DateTime(2026, 9, 30, 10, 5, 0, DateTimeKind.Utc), employee.UserId);
        AddActivity(task, WebsiteWorkReviewService.ApprovedAction, new DateTime(2026, 9, 30, 10, 10, 0, DateTimeKind.Utc), manager.Id, "{\"comment\":\"Checked and approved.\"}");
        AddActivity(task, WebsiteWorkService.CompletedAction, new DateTime(2026, 9, 30, 10, 10, 0, DateTimeKind.Utc), manager.Id);
        task.Status = ProjectTaskStatus.Done;
        task.CompletedAtUtc = new DateTime(2026, 9, 30, 10, 10, 0, DateTimeKind.Utc);
        task.UpdatedAtUtc = task.CompletedAtUtc.Value;
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkTimelineService(db, new FixedTimeProvider(now));
        var result = await service.GetEmployeeAsync(
            employee.Id,
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30),
            0,
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var timeline = Assert.IsType<WebsiteWorkEmployeeTimelineResponse>(result.Value);
        Assert.Equal(employee.Id, timeline.EmployeeId);
        Assert.Equal("Jihad", timeline.FullName);

        var item = Assert.Single(timeline.Items);
        Assert.Equal(task.Id, item.TaskId);
        Assert.Equal(45 * 60, item.WorkingSecondsInPeriod);
        Assert.Equal(45 * 60, item.TotalWorkingSeconds);
        Assert.Equal(1, item.CorrectionCount);
        Assert.Equal(new DateTime(2026, 9, 30, 9, 10, 0, DateTimeKind.Utc), item.FirstStartedAtUtc);
        Assert.Equal(new DateTime(2026, 9, 30, 10, 5, 0, DateTimeKind.Utc), item.LastSubmittedAtUtc);
        Assert.Equal(new DateTime(2026, 9, 30, 10, 10, 0, DateTimeKind.Utc), item.ApprovedAtUtc);
        Assert.False(item.OverdueAtPeriodEnd);

        Assert.Equal(6, item.Events.Count);
        Assert.Single(item.Events, x => x.Type == WebsiteWorkTimelineEventType.Assigned);
        Assert.Single(item.Events, x => x.Type == WebsiteWorkTimelineEventType.Started);
        Assert.Equal(2, item.Events.Count(x => x.Type == WebsiteWorkTimelineEventType.Submitted));
        var correction = Assert.Single(item.Events, x => x.Type == WebsiteWorkTimelineEventType.CorrectionRequested);
        Assert.Equal("Please correct the final amount.", correction.Detail);
        Assert.Equal("boss@example.com", correction.ActorEmail);
        var approved = Assert.Single(item.Events, x => x.Type == WebsiteWorkTimelineEventType.Approved);
        Assert.Equal("Checked and approved.", approved.Detail);
        Assert.Equal("boss@example.com", approved.ActorEmail);
        Assert.DoesNotContain(item.Events, x => x.Type == WebsiteWorkTimelineEventType.Completed);
    }

    [Fact]
    public async Task Timeline_includes_open_work_that_crosses_into_selected_period()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 1, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-TIME-2", "Night Worker", "night@example.com");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, employee.UserId, new DateTime(2026, 9, 29, 23, 20, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkService.ConfiguredAction, task.CreatedAtUtc, employee.UserId);
        AddActivity(task, WebsiteWorkService.StartedAction, new DateTime(2026, 9, 29, 23, 30, 0, DateTimeKind.Utc), employee.UserId);
        task.Status = ProjectTaskStatus.InProgress;
        task.UpdatedAtUtc = new DateTime(2026, 9, 29, 23, 30, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkTimelineService(db, new FixedTimeProvider(now));
        var result = await service.GetEmployeeAsync(
            employee.Id,
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30),
            0,
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(60 * 60, item.WorkingSecondsInPeriod);
        Assert.Equal(90 * 60, item.TotalWorkingSeconds);
        Assert.Equal(ProjectTaskStatus.InProgress, item.Status);
    }

    [Fact]
    public async Task Timeline_rejects_future_range_and_missing_employee()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var service = new WebsiteWorkTimelineService(db, new FixedTimeProvider(now));

        var missing = await service.GetEmployeeAsync(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30),
            0,
            cancellationToken);
        Assert.Equal(OperationStatus.NotFound, missing.Status);
        Assert.Equal("employee_not_found", missing.ErrorCode);

        var future = await service.GetEmployeeAsync(
            Guid.NewGuid(),
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 10, 1),
            0,
            cancellationToken);
        Assert.Equal(OperationStatus.Invalid, future.Status);
        Assert.Equal("report_range_future", future.ErrorCode);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"website-work-timeline-{Guid.NewGuid():N}")
            .Options);

    private static Employee AddEmployee(AppDbContext db, string code, string name, string email)
    {
        var department = new Department
        {
            Code = $"D-{code}",
            NormalizedCode = $"D-{code}".ToUpperInvariant(),
            Name = "Survey Team",
            NormalizedName = "SURVEY TEAM",
            IsActive = true
        };
        var user = AddUser(db, email);
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
        db.Employees.Add(employee);
        return employee;
    }

    private static User AddUser(AppDbContext db, string email)
    {
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "hash",
            IsActive = true
        };
        db.Users.Add(user);
        return user;
    }

    private static Project AddProject(AppDbContext db)
    {
        var code = $"TIME-{Guid.NewGuid():N}"[..12];
        var project = new Project
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = "Website Timeline",
            NormalizedName = "WEBSITE TIMELINE",
            Status = ProjectStatus.Active
        };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectTask AddWebsiteWork(
        AppDbContext db,
        Employee employee,
        Project project,
        Guid createdByUserId,
        DateTime createdAtUtc)
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
            CreatedByUserId = createdByUserId,
            DueDate = new DateOnly(2026, 10, 1),
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
        db.ProjectTasks.Add(task);
        return task;
    }

    private static void AddActivity(
        ProjectTask task,
        string action,
        DateTime atUtc,
        Guid? actorUserId,
        string detailsJson = "{}")
    {
        task.Activities.Add(new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            ActorUserId = actorUserId,
            Action = action,
            DetailsJson = detailsJson,
            CreatedAtUtc = atUtc
        });
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
