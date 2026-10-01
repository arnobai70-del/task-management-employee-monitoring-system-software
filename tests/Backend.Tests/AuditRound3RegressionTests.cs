using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Controllers;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AuditRound3RegressionTests
{
    [Fact]
    public async Task Generic_task_mutations_reject_website_work()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = "Website work task",
            NormalizedTitle = "WEBSITE WORK TASK",
            Status = ProjectTaskStatus.InProgress,
            Priority = ProjectTaskPriority.Normal,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        db.ProjectTasks.Add(task);
        db.TaskActivities.Add(new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            Action = WebsiteWorkService.ConfiguredAction,
            DetailsJson = "{\"url\":\"https://example.com/work\"}",
            CreatedAtUtc = UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);

        var controller = new TasksController(
            new ProjectTaskCoreService(db, new MutableTimeProvider(new DateTimeOffset(UtcNow))),
            db);

        var update = await controller.Update(task.Id, new UpdateProjectTaskRequest
        {
            Title = "Changed through generic tasks",
            Priority = ProjectTaskPriority.High
        }, cancellationToken);
        var updateConflict = Assert.IsType<ConflictObjectResult>(update.Result);
        Assert.Equal("website_work_managed_separately", Assert.IsType<ApiOperationError>(updateConflict.Value).Code);

        var status = await controller.ChangeStatus(task.Id, new ChangeProjectTaskStatusRequest
        {
            Status = ProjectTaskStatus.Done
        }, cancellationToken);
        var statusConflict = Assert.IsType<ConflictObjectResult>(status.Result);
        Assert.Equal("website_work_managed_separately", Assert.IsType<ApiOperationError>(statusConflict.Value).Code);

        var stored = await db.ProjectTasks.SingleAsync(x => x.Id == task.Id, cancellationToken);
        Assert.Equal(ProjectTaskStatus.InProgress, stored.Status);
        Assert.Equal("Website work task", stored.Title);
    }

    [Fact]
    public async Task Survey_workspace_hides_expired_and_future_links_and_expired_link_cannot_open()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (user, employee) = AddEmployee(db, "SUR-R3-1", "Survey Regression One");
        var current = AddSurvey(db, employee, "Current survey", "https://survey.example.com/current", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5));
        var expired = AddSurvey(db, employee, "Expired survey", "https://survey.example.com/expired", new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1));
        AddSurvey(db, employee, "Future survey", "https://survey.example.com/future", new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 10));
        await db.SaveChangesAsync(cancellationToken);

        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-02T06:00:00Z"));
        var service = new ExternalSurveyService(db, clock);
        var mine = await service.GetMineAsync(new RequestActor(user.Id, null, "tests"), includeInactive: false, cancellationToken);

        Assert.Equal(OperationStatus.Success, mine.Status);
        var visible = Assert.Single(mine.Value!);
        Assert.Equal(current.Id, visible.Id);

        var openExpired = await service.RecordOpenAsync(expired.Id, new RequestActor(user.Id, "127.0.0.1", "tests"), cancellationToken);
        Assert.Equal(OperationStatus.Conflict, openExpired.Status);
        Assert.Equal("survey_link_expired", openExpired.ErrorCode);
        Assert.Empty(await db.AuditLogs.Where(x => x.Action == "survey_link.opened").ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task Expired_survey_does_not_block_non_overlapping_renewal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (_, employee) = AddEmployee(db, "SUR-R3-2", "Survey Regression Two");
        AddSurvey(db, employee, "Old survey window", "https://survey.example.com/renew", new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1));
        await db.SaveChangesAsync(cancellationToken);

        var service = new ExternalSurveyService(db, new MutableTimeProvider(DateTimeOffset.Parse("2026-10-02T06:00:00Z")));
        var renewed = await service.CreateAsync(new UpsertExternalSurveyAssignmentRequest
        {
            EmployeeId = employee.Id,
            Title = "Renewed survey window",
            Url = "https://survey.example.com/renew",
            StartsOn = new DateOnly(2026, 10, 2),
            DueDate = new DateOnly(2026, 10, 10),
            IsActive = true
        }, new RequestActor(Guid.NewGuid(), "127.0.0.1", "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Success, renewed.Status);
        Assert.Equal(2, await db.Set<WebsiteAssignment>().CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Attendance_check_in_is_rejected_before_start_and_at_or_after_shift_end()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (user, employee) = AddEmployee(db, "ATT-R3-1", "Attendance Regression");
        var shift = new Shift
        {
            Code = "DAY-R3",
            NormalizedCode = "DAY-R3",
            Name = "Round Three Day Shift",
            NormalizedName = "ROUND THREE DAY SHIFT",
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            TimeZoneId = "Asia/Dhaka",
            GraceMinutes = 10,
            IsActive = true
        };
        db.Shifts.Add(shift);
        db.EmployeeShiftAssignments.Add(new EmployeeShiftAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            EffectiveFrom = new DateOnly(2026, 10, 2),
            EffectiveTo = new DateOnly(2026, 10, 2)
        });
        await db.SaveChangesAsync(cancellationToken);

        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-02T02:59:00Z"));
        var service = new AttendanceCoreService(db, clock);
        var actor = new RequestActor(user.Id, "127.0.0.1", "tests");

        var beforeStart = await service.CheckInAsync(actor, cancellationToken);
        Assert.Equal(OperationStatus.Conflict, beforeStart.Status);
        Assert.Equal("shift_not_started", beforeStart.ErrorCode);

        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-02T11:00:00Z"));
        var afterEnd = await service.CheckInAsync(actor, cancellationToken);
        Assert.Equal(OperationStatus.Conflict, afterEnd.Status);
        Assert.Equal("shift_check_in_closed", afterEnd.ErrorCode);
        Assert.Empty(await db.WorkSessions.ToListAsync(cancellationToken));
    }

    private static readonly DateTime UtcNow = DateTime.Parse("2026-10-02T06:00:00Z").ToUniversalTime();

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static Project AddProject(AppDbContext db)
    {
        var project = new Project
        {
            Code = "R3-PROJECT",
            NormalizedCode = "R3-PROJECT",
            Name = "Round Three Project",
            NormalizedName = "ROUND THREE PROJECT",
            Status = ProjectStatus.Active,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        db.Projects.Add(project);
        return project;
    }

    private static (User User, Employee Employee) AddEmployee(AppDbContext db, string code, string name)
    {
        var user = new User
        {
            Email = $"{code.ToLowerInvariant()}@example.com",
            NormalizedEmail = $"{code.ToUpperInvariant()}@EXAMPLE.COM",
            PasswordHash = "test-hash",
            IsActive = true,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = name,
            NormalizedFullName = name.ToUpperInvariant(),
            JobTitle = "Regression Tester",
            IsActive = true,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        db.AddRange(user, employee);
        return (user, employee);
    }

    private static WebsiteAssignment AddSurvey(
        AppDbContext db,
        Employee employee,
        string name,
        string url,
        DateOnly? startsOn,
        DateOnly? expiresOn)
    {
        var assignment = new WebsiteAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            Name = name,
            Url = url,
            AccessLevel = WebsiteAccessLevel.Survey,
            StartsOn = startsOn,
            ExpiresOn = expiresOn,
            IsActive = true,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        db.Set<WebsiteAssignment>().Add(assignment);
        return assignment;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }
}
