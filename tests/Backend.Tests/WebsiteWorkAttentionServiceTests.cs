using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class WebsiteWorkAttentionServiceTests
{
    [Fact]
    public async Task Attention_combines_overdue_long_working_and_repeated_correction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "ATT-1", "Jihad");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, ProjectTaskStatus.InProgress, new DateOnly(2026, 9, 29));

        AddActivity(task, WebsiteWorkService.ConfiguredAction, new DateTime(2026, 9, 30, 7, 0, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkService.StartedAction, new DateTime(2026, 9, 30, 7, 15, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.SubmittedAction, new DateTime(2026, 9, 30, 7, 45, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.ReopenedAction, new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.SubmittedAction, new DateTime(2026, 9, 30, 8, 20, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.ReopenedAction, new DateTime(2026, 9, 30, 9, 30, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db, now);
        var result = await service.GetAsync(0, 20, cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var response = Assert.IsType<WebsiteWorkAttentionResponse>(result.Value);
        var item = Assert.Single(response.Items);
        Assert.Equal(WebsiteWorkAttentionSeverity.Critical, item.Severity);
        Assert.Equal(2, item.CorrectionCount);
        Assert.Equal(150 * 60L, item.CurrentWorkingSeconds);
        Assert.Contains(item.Reasons, x => x.Type == WebsiteWorkAttentionReasonType.Overdue);
        Assert.Contains(item.Reasons, x => x.Type == WebsiteWorkAttentionReasonType.LongWorking);
        Assert.Contains(item.Reasons, x => x.Type == WebsiteWorkAttentionReasonType.RepeatedCorrection);
    }

    [Fact]
    public async Task Attention_flags_pending_review_after_threshold()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "ATT-2", "Reviewer Wait");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, ProjectTaskStatus.Blocked, new DateOnly(2026, 10, 2));

        AddActivity(task, WebsiteWorkService.ConfiguredAction, new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkService.StartedAction, new DateTime(2026, 9, 30, 9, 10, 0, DateTimeKind.Utc));
        AddActivity(task, WebsiteWorkReviewService.SubmittedAction, new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db, now);
        var result = await service.GetAsync(0, 20, cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(WebsiteWorkAttentionSeverity.High, item.Severity);
        Assert.Equal(120 * 60L, item.PendingReviewSeconds);
        Assert.Contains(item.Reasons, x => x.Type == WebsiteWorkAttentionReasonType.PendingReview);
        Assert.DoesNotContain(item.Reasons, x => x.Type == WebsiteWorkAttentionReasonType.LongWorking);
    }

    [Fact]
    public async Task Attention_counts_all_alerts_even_when_rows_are_limited()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var project = AddProject(db);

        for (var index = 0; index < 2; index++)
        {
            var employee = AddEmployee(db, $"ATT-L{index}", $"Worker {index}");
            var task = AddWebsiteWork(db, employee, project, ProjectTaskStatus.ToDo, new DateOnly(2026, 9, 28));
            AddActivity(task, WebsiteWorkService.ConfiguredAction, new DateTime(2026, 9, 29, 8, index, 0, DateTimeKind.Utc));
        }
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db, now);
        var result = await service.GetAsync(0, 1, cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(2, result.Value!.Total);
        Assert.Equal(2, result.Value.Critical);
        Assert.Single(result.Value.Items);
    }

    [Fact]
    public async Task Attention_rejects_invalid_offset_and_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var service = CreateService(db, new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));

        var invalidOffset = await service.GetAsync(841, 20, cancellationToken);
        var invalidLimit = await service.GetAsync(0, 101, cancellationToken);

        Assert.Equal(OperationStatus.Invalid, invalidOffset.Status);
        Assert.Equal("utc_offset_invalid", invalidOffset.ErrorCode);
        Assert.Equal(OperationStatus.Invalid, invalidLimit.Status);
        Assert.Equal("attention_limit_invalid", invalidLimit.ErrorCode);
    }

    private static WebsiteWorkAttentionService CreateService(AppDbContext db, DateTime now)
        => new(
            db,
            new FixedTimeProvider(now),
            Options.Create(new WebsiteWorkAttentionOptions
            {
                LongWorkingMinutes = 120,
                PendingReviewMinutes = 60,
                RepeatedCorrectionCount = 2
            }));

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"website-work-attention-{Guid.NewGuid():N}")
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
        var code = $"ATT-{Guid.NewGuid():N}"[..12];
        var project = new Project
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = "Website Attention",
            NormalizedName = "WEBSITE ATTENTION",
            Status = ProjectStatus.Active
        };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectTask AddWebsiteWork(
        AppDbContext db,
        Employee employee,
        Project project,
        ProjectTaskStatus status,
        DateOnly? dueDate)
    {
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = $"Target for {employee.FullName}",
            NormalizedTitle = $"TARGET FOR {employee.FullName}".ToUpperInvariant(),
            Status = status,
            Priority = ProjectTaskPriority.Normal,
            AssigneeEmployeeId = employee.Id,
            AssigneeEmployee = employee,
            DueDate = dueDate,
            CreatedAtUtc = new DateTime(2026, 9, 29, 7, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 9, 30, 9, 30, 0, DateTimeKind.Utc)
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
