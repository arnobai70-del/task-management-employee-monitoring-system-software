using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class WebsiteWorkReviewServiceTests
{
    [Fact]
    public async Task Employee_submission_waits_for_review_and_manager_can_approve()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new MutableTimeProvider(new DateTime(2026, 9, 30, 5, 0, 0, DateTimeKind.Utc));
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-201", "Jihad");
        var task = AddWebsiteWork(db, employee, "InboxDollars - complete $5 target");
        await db.SaveChangesAsync(cancellationToken);

        var publisher = new CapturePublisher();
        var service = new WebsiteWorkReviewService(db, clock, publisher, NullLogger<WebsiteWorkReviewService>.Instance);
        var employeeActor = new RequestActor(employee.UserId, "127.0.0.1", "tests");

        var submitted = await service.SubmitAsync(task.Id, employeeActor, cancellationToken);
        Assert.Equal(OperationStatus.Success, submitted.Status);
        Assert.Equal(ProjectTaskStatus.Blocked, submitted.Value!.Status);
        Assert.Equal(WebsiteWorkReviewStates.PendingReview, submitted.Value.ReviewState);
        Assert.NotNull(submitted.Value.SubmittedAtUtc);
        Assert.Contains("submitted completion", Assert.Single(publisher.Completions).Message);

        clock.Advance(TimeSpan.FromMinutes(2));
        var approved = await service.ApproveAsync(
            task.Id,
            new ApproveWebsiteWorkRequest { Comment = "Target checked and accepted." },
            new RequestActor(Guid.NewGuid(), "127.0.0.2", "manager-tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, approved.Status);
        Assert.Equal(WebsiteWorkReviewStates.Approved, approved.Value!.State);
        Assert.Equal("Target checked and accepted.", approved.Value.Comment);
        Assert.Equal(ProjectTaskStatus.Done, task.Status);

        var enriched = await service.EnrichAsync([submitted.Value], cancellationToken);
        var reviewed = Assert.Single(enriched);
        Assert.Equal(WebsiteWorkReviewStates.Approved, reviewed.ReviewState);
        Assert.Equal("Target checked and accepted.", reviewed.ReviewComment);
        Assert.NotNull(reviewed.ReviewedAtUtc);
    }

    [Fact]
    public async Task Manager_can_reopen_with_comment_and_employee_can_resubmit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new MutableTimeProvider(new DateTime(2026, 9, 30, 6, 0, 0, DateTimeKind.Utc));
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-202", "Nila");
        var task = AddWebsiteWork(db, employee, "Complete external target");
        await db.SaveChangesAsync(cancellationToken);

        var publisher = new CapturePublisher();
        var service = new WebsiteWorkReviewService(db, clock, publisher, NullLogger<WebsiteWorkReviewService>.Instance);
        var employeeActor = new RequestActor(employee.UserId, null, "tests");

        var firstSubmission = await service.SubmitAsync(task.Id, employeeActor, cancellationToken);
        Assert.Equal(OperationStatus.Success, firstSubmission.Status);

        clock.Advance(TimeSpan.FromMinutes(1));
        var reopened = await service.ReopenAsync(
            task.Id,
            new ReopenWebsiteWorkRequest { Comment = "Please complete the final verification step." },
            new RequestActor(Guid.NewGuid(), null, "manager-tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, reopened.Status);
        Assert.Equal(WebsiteWorkReviewStates.CorrectionRequired, reopened.Value!.State);
        Assert.Equal("Please complete the final verification step.", reopened.Value.Comment);
        Assert.Equal(ProjectTaskStatus.InProgress, task.Status);
        Assert.Null(task.CompletedAtUtc);

        var response = new WebsiteWorkResponse(
            task.Id,
            task.ProjectId,
            task.Project.Code,
            task.Project.Name,
            task.AssigneeEmployeeId,
            employee.EmployeeCode,
            employee.FullName,
            task.Title,
            task.Description,
            "https://work.example.com/target",
            task.Status,
            task.Priority,
            task.DueDate,
            null,
            task.CompletedAtUtc,
            task.CreatedAtUtc,
            task.UpdatedAtUtc);
        var enriched = Assert.Single(await service.EnrichAsync([response], cancellationToken));
        Assert.Equal(WebsiteWorkReviewStates.CorrectionRequired, enriched.ReviewState);
        Assert.Equal("Please complete the final verification step.", enriched.ReviewComment);

        clock.Advance(TimeSpan.FromMinutes(10));
        var secondSubmission = await service.SubmitAsync(task.Id, employeeActor, cancellationToken);
        Assert.Equal(OperationStatus.Success, secondSubmission.Status);
        Assert.Equal(WebsiteWorkReviewStates.PendingReview, secondSubmission.Value!.ReviewState);
        Assert.Equal(ProjectTaskStatus.Blocked, task.Status);
        Assert.Equal(2, publisher.Completions.Count);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"website-work-review-{Guid.NewGuid():N}")
            .Options);

    private static Employee AddEmployee(AppDbContext db, string code, string name)
    {
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
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = name,
            NormalizedFullName = name.ToUpperInvariant(),
            JobTitle = "Worker",
            IsActive = true
        };
        db.Users.Add(user);
        db.Employees.Add(employee);
        return employee;
    }

    private static ProjectTask AddWebsiteWork(AppDbContext db, Employee employee, string title)
    {
        var project = new Project
        {
            Code = $"REV-{Guid.NewGuid():N}"[..12],
            NormalizedCode = $"REV-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Review Targets",
            NormalizedName = "REVIEW TARGETS",
            Status = ProjectStatus.Active
        };
        var now = new DateTime(2026, 9, 30, 4, 0, 0, DateTimeKind.Utc);
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = title,
            NormalizedTitle = title.ToUpperInvariant(),
            Description = "Complete the assigned external target.",
            Status = ProjectTaskStatus.InProgress,
            Priority = ProjectTaskPriority.Normal,
            AssigneeEmployeeId = employee.Id,
            AssigneeEmployee = employee,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Projects.Add(project);
        db.ProjectTasks.Add(task);
        AddActivity(db, task, WebsiteWorkService.ConfiguredAction, now, new { url = "https://work.example.com/target" });
        AddActivity(db, task, WebsiteWorkService.StartedAction, now, new { host = "work.example.com" });
        return task;
    }

    private static void AddActivity(AppDbContext db, ProjectTask task, string action, DateTime createdAtUtc, object details)
    {
        var activity = new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            Action = action,
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAtUtc = createdAtUtc
        };
        db.TaskActivities.Add(activity);
        task.Activities.Add(activity);
    }

    private sealed class CapturePublisher : IWebsiteWorkRealtimePublisher
    {
        public List<WebsiteWorkCompletionResponse> Completions { get; } = [];

        public Task PublishCompletionAsync(WebsiteWorkCompletionResponse completion, CancellationToken cancellationToken)
        {
            Completions.Add(completion);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => new(_utcNow);
        public void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
    }
}
