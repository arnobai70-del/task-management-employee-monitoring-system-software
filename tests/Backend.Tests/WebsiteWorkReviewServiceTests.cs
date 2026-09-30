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
        var now = new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var (employee, task) = SeedWorkingWebsiteWork(db, now.AddMinutes(-10));
        await db.SaveChangesAsync(cancellationToken);

        var managerPublisher = new CaptureWebsitePublisher();
        var employeePublisher = new CaptureRealtimePublisher();
        var service = new WebsiteWorkReviewService(
            db,
            new FixedTimeProvider(now),
            managerPublisher,
            employeePublisher,
            NullLogger<WebsiteWorkReviewService>.Instance);

        var submitted = await service.SubmitAsync(task.Id, new RequestActor(employee.UserId, null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.Success, submitted.Status);
        Assert.Equal(ProjectTaskStatus.Blocked, submitted.Value!.Status);
        var managerEvent = Assert.Single(managerPublisher.Submissions);
        Assert.Contains("submitted completion", managerEvent.Message, StringComparison.OrdinalIgnoreCase);

        var approved = await service.ApproveAsync(
            task.Id,
            new WebsiteWorkReviewRequest { Comment = "Checked and approved." },
            new RequestActor(Guid.NewGuid(), null, "manager-tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, approved.Status);
        Assert.Equal(ProjectTaskStatus.Done, approved.Value!.Status);
        Assert.NotNull(approved.Value.CompletedAtUtc);
        Assert.Contains(db.TaskActivities, x => x.ProjectTaskId == task.Id && x.Action == WebsiteWorkService.CompletedAction);
        Assert.Contains(db.TaskActivities, x => x.ProjectTaskId == task.Id && x.Action == WebsiteWorkReviewService.ApprovedAction);

        var notification = Assert.Single(employeePublisher.Notifications);
        Assert.Equal(employee.Id, notification.EmployeeId);
        Assert.Contains("approved", notification.Notification.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Manager_reopen_requires_comment_and_returns_work_to_in_progress()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 15, 30, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var (employee, task) = SeedWorkingWebsiteWork(db, now.AddMinutes(-20));
        task.Status = ProjectTaskStatus.Blocked;
        await db.SaveChangesAsync(cancellationToken);

        var employeePublisher = new CaptureRealtimePublisher();
        var service = new WebsiteWorkReviewService(
            db,
            new FixedTimeProvider(now),
            new CaptureWebsitePublisher(),
            employeePublisher,
            NullLogger<WebsiteWorkReviewService>.Instance);

        var missingComment = await service.ReopenAsync(
            task.Id,
            new WebsiteWorkReviewRequest { Comment = " " },
            new RequestActor(Guid.NewGuid(), null, "manager-tests"),
            cancellationToken);
        Assert.Equal(OperationStatus.Invalid, missingComment.Status);
        Assert.Equal("review_comment_required", missingComment.ErrorCode);

        var reopened = await service.ReopenAsync(
            task.Id,
            new WebsiteWorkReviewRequest { Comment = "Please correct the final amount and resubmit." },
            new RequestActor(Guid.NewGuid(), null, "manager-tests"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, reopened.Status);
        Assert.Equal(ProjectTaskStatus.InProgress, reopened.Value!.Status);
        Assert.Contains(db.TaskActivities, x => x.ProjectTaskId == task.Id && x.Action == WebsiteWorkReviewService.ReopenedAction);
        var notification = Assert.Single(employeePublisher.Notifications);
        Assert.Equal(employee.Id, notification.EmployeeId);
        Assert.Contains("correction", notification.Notification.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Another_employee_cannot_submit_someone_elses_website_work()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var (owner, task) = SeedWorkingWebsiteWork(db, DateTime.UtcNow.AddMinutes(-5));
        var other = AddEmployee(db, "WEB-REVIEW-2", "Other Worker");
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkReviewService(
            db,
            TimeProvider.System,
            new CaptureWebsitePublisher(),
            new CaptureRealtimePublisher(),
            NullLogger<WebsiteWorkReviewService>.Instance);

        var result = await service.SubmitAsync(task.Id, new RequestActor(other.UserId, null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.NotFound, result.Status);
        Assert.Equal("website_work_not_found", result.ErrorCode);
        Assert.Equal(ProjectTaskStatus.InProgress, task.Status);
        Assert.NotEqual(owner.Id, other.Id);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"website-work-review-{Guid.NewGuid():N}")
            .Options);

    private static (Employee Employee, ProjectTask Task) SeedWorkingWebsiteWork(AppDbContext db, DateTime startedAtUtc)
    {
        var employee = AddEmployee(db, "WEB-REVIEW-1", "Jihad");
        var project = new Project
        {
            Code = "WEB-REV",
            NormalizedCode = "WEB-REV",
            Name = "Website Review",
            NormalizedName = "WEBSITE REVIEW",
            Status = ProjectStatus.Active
        };
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
            CreatedAtUtc = startedAtUtc.AddMinutes(-5),
            UpdatedAtUtc = startedAtUtc
        };
        task.Activities.Add(new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            ActorUserId = employee.UserId,
            Action = WebsiteWorkService.ConfiguredAction,
            DetailsJson = "{\"url\":\"https://example.com/work\"}",
            CreatedAtUtc = startedAtUtc.AddMinutes(-5)
        });
        task.Activities.Add(new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            ActorUserId = employee.UserId,
            Action = WebsiteWorkService.StartedAction,
            DetailsJson = "{\"host\":\"example.com\"}",
            CreatedAtUtc = startedAtUtc
        });
        db.Projects.Add(project);
        db.ProjectTasks.Add(task);
        return (employee, task);
    }

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

    private sealed class CaptureWebsitePublisher : IWebsiteWorkRealtimePublisher
    {
        public List<WebsiteWorkSubmissionResponse> Submissions { get; } = [];
        public Task PublishCompletionAsync(WebsiteWorkCompletionResponse completion, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task PublishSubmissionAsync(WebsiteWorkSubmissionResponse submission, CancellationToken cancellationToken)
        {
            Submissions.Add(submission);
            return Task.CompletedTask;
        }
    }

    private sealed class CaptureRealtimePublisher : IRealtimeEventPublisher
    {
        public List<(Guid EmployeeId, EmployeeNotificationResponse Notification)> Notifications { get; } = [];
        public Task PublishPresenceAsync(EmployeePresenceResponse presence, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task PublishNotificationAsync(Guid employeeId, EmployeeNotificationResponse notification, CancellationToken cancellationToken)
        {
            Notifications.Add((employeeId, notification));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
