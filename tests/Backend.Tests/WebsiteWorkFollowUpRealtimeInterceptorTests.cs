using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Infrastructure;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class WebsiteWorkFollowUpRealtimeInterceptorTests
{
    [Fact]
    public async Task Realtime_follow_up_events_track_assign_reassign_resolve_and_lifecycle_removal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var publisher = new CapturingPublisher();
        var interceptor = new WebsiteWorkFollowUpRealtimeInterceptor(
            publisher,
            NullLogger<WebsiteWorkFollowUpRealtimeInterceptor>.Instance);
        await using var db = CreateDb(interceptor);

        var worker = AddEmployee(db, "RT-FU-1", "Jihad");
        var project = AddProject(db);
        var task = new ProjectTask
        {
            Project = project,
            ProjectId = project.Id,
            AssigneeEmployee = worker,
            AssigneeEmployeeId = worker.Id,
            Title = "InboxDollars $5 target",
            NormalizedTitle = "INBOXDOLLARS $5 TARGET",
            Status = ProjectTaskStatus.ToDo,
            Priority = ProjectTaskPriority.Normal,
            CreatedAtUtc = Utc(8),
            UpdatedAtUtc = Utc(8)
        };
        db.ProjectTasks.Add(task);
        AddActivity(db, task, WebsiteWorkService.ConfiguredAction, Utc(8), new { url = "https://example.com/work" });
        await db.SaveChangesAsync(cancellationToken);
        Assert.Empty(publisher.Events);

        var ownerOne = Guid.NewGuid();
        var ownerTwo = Guid.NewGuid();
        AddFollowUp(db, task, ownerOne, "manager-one@example.com", "Manager One", Utc(10), Utc(9));
        await db.SaveChangesAsync(cancellationToken);

        var assigned = Assert.Single(publisher.Events);
        Assert.Equal(ownerOne, assigned.UserId);
        Assert.Equal(WebsiteWorkFollowUpRealtimeAction.Assigned, assigned.Payload.Action);
        Assert.Equal(task.Id, assigned.Payload.TaskId);
        publisher.Events.Clear();

        AddFollowUp(db, task, ownerTwo, "manager-two@example.com", "Manager Two", Utc(11), Utc(9, 15));
        await db.SaveChangesAsync(cancellationToken);

        Assert.Equal(2, publisher.Events.Count);
        Assert.Contains(publisher.Events, item =>
            item.UserId == ownerOne && item.Payload.Action == WebsiteWorkFollowUpRealtimeAction.Removed);
        Assert.Contains(publisher.Events, item =>
            item.UserId == ownerTwo && item.Payload.Action == WebsiteWorkFollowUpRealtimeAction.Assigned);
        publisher.Events.Clear();

        AddActivity(db, task, WebsiteWorkAttentionActionService.FollowUpResolvedAction, Utc(9, 30), new
        {
            actorUserId = ownerTwo,
            actorEmail = "manager-two@example.com",
            followUpOwnerUserId = ownerTwo,
            followUpOwnerEmail = "manager-two@example.com",
            followUpOwnerName = "Manager Two",
            followUpDueAtUtc = Utc(11),
            resolvedAtUtc = Utc(9, 30)
        });
        await db.SaveChangesAsync(cancellationToken);

        var resolved = Assert.Single(publisher.Events);
        Assert.Equal(ownerTwo, resolved.UserId);
        Assert.Equal(WebsiteWorkFollowUpRealtimeAction.Resolved, resolved.Payload.Action);
        publisher.Events.Clear();

        AddFollowUp(db, task, ownerTwo, "manager-two@example.com", "Manager Two", Utc(12), Utc(9, 45));
        await db.SaveChangesAsync(cancellationToken);
        publisher.Events.Clear();

        AddActivity(db, task, WebsiteWorkService.StartedAction, Utc(10));
        task.Status = ProjectTaskStatus.InProgress;
        task.UpdatedAtUtc = Utc(10);
        await db.SaveChangesAsync(cancellationToken);

        var removedByLifecycle = Assert.Single(publisher.Events);
        Assert.Equal(ownerTwo, removedByLifecycle.UserId);
        Assert.Equal(WebsiteWorkFollowUpRealtimeAction.Removed, removedByLifecycle.Payload.Action);
    }

    [Fact]
    public async Task Realtime_follow_up_same_owner_assignment_is_an_update()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var publisher = new CapturingPublisher();
        var interceptor = new WebsiteWorkFollowUpRealtimeInterceptor(
            publisher,
            NullLogger<WebsiteWorkFollowUpRealtimeInterceptor>.Instance);
        await using var db = CreateDb(interceptor);

        var worker = AddEmployee(db, "RT-FU-2", "Worker");
        var project = AddProject(db);
        var task = new ProjectTask
        {
            Project = project,
            ProjectId = project.Id,
            AssigneeEmployee = worker,
            AssigneeEmployeeId = worker.Id,
            Title = "Website target",
            NormalizedTitle = "WEBSITE TARGET",
            Status = ProjectTaskStatus.ToDo,
            Priority = ProjectTaskPriority.Normal,
            CreatedAtUtc = Utc(8),
            UpdatedAtUtc = Utc(8)
        };
        db.ProjectTasks.Add(task);
        AddActivity(db, task, WebsiteWorkService.ConfiguredAction, Utc(8));
        await db.SaveChangesAsync(cancellationToken);

        var owner = Guid.NewGuid();
        AddFollowUp(db, task, owner, "manager@example.com", "Manager", Utc(10), Utc(9));
        await db.SaveChangesAsync(cancellationToken);
        publisher.Events.Clear();

        AddFollowUp(db, task, owner, "manager@example.com", "Manager", Utc(12), Utc(9, 30));
        await db.SaveChangesAsync(cancellationToken);

        var updated = Assert.Single(publisher.Events);
        Assert.Equal(owner, updated.UserId);
        Assert.Equal(WebsiteWorkFollowUpRealtimeAction.Updated, updated.Payload.Action);
        Assert.Equal(Utc(12), updated.Payload.DueAtUtc);
    }

    private static AppDbContext CreateDb(WebsiteWorkFollowUpRealtimeInterceptor interceptor)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"follow-up-realtime-{Guid.NewGuid():N}")
            .AddInterceptors(interceptor)
            .Options);

    private static Employee AddEmployee(AppDbContext db, string code, string name)
    {
        var department = new Department
        {
            Code = $"D-{code}",
            NormalizedCode = $"D-{code}".ToUpperInvariant(),
            Name = "Operations",
            NormalizedName = "OPERATIONS",
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
            User = user,
            UserId = user.Id,
            Department = department,
            DepartmentId = department.Id,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = name,
            NormalizedFullName = name.ToUpperInvariant(),
            JobTitle = "Worker",
            IsActive = true
        };
        user.Employee = employee;
        db.Departments.Add(department);
        db.Users.Add(user);
        db.Employees.Add(employee);
        return employee;
    }

    private static Project AddProject(AppDbContext db)
    {
        var code = $"FU-{Guid.NewGuid():N}"[..12];
        var project = new Project
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = "Follow-up Realtime",
            NormalizedName = "FOLLOW-UP REALTIME",
            Status = ProjectStatus.Active
        };
        db.Projects.Add(project);
        return project;
    }

    private static void AddFollowUp(
        AppDbContext db,
        ProjectTask task,
        Guid ownerUserId,
        string ownerEmail,
        string ownerName,
        DateTime dueAtUtc,
        DateTime atUtc)
        => AddActivity(db, task, WebsiteWorkAttentionActionService.FollowUpAssignedAction, atUtc, new
        {
            actorUserId = Guid.NewGuid(),
            actorEmail = "boss@example.com",
            note = "Check this target.",
            followUpOwnerUserId = ownerUserId,
            followUpOwnerEmail = ownerEmail,
            followUpOwnerName = ownerName,
            followUpDueAtUtc = dueAtUtc
        });

    private static void AddActivity(
        AppDbContext db,
        ProjectTask task,
        string action,
        DateTime atUtc,
        object? details = null)
    {
        var activity = new TaskActivity
        {
            ProjectTask = task,
            ProjectTaskId = task.Id,
            Action = action,
            DetailsJson = details is null ? "{}" : JsonSerializer.Serialize(details),
            CreatedAtUtc = atUtc
        };
        task.Activities.Add(activity);
        db.TaskActivities.Add(activity);
    }

    private static DateTime Utc(int hour, int minute = 0)
        => new(2026, 10, 1, hour, minute, 0, DateTimeKind.Utc);

    private sealed class CapturingPublisher : IWebsiteWorkRealtimePublisher
    {
        public List<(Guid UserId, WebsiteWorkFollowUpRealtimeResponse Payload)> Events { get; } = [];

        public Task PublishCompletionAsync(WebsiteWorkCompletionResponse completion, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task PublishFollowUpAsync(
            Guid userId,
            WebsiteWorkFollowUpRealtimeResponse followUp,
            CancellationToken cancellationToken)
        {
            Events.Add((userId, followUp));
            return Task.CompletedTask;
        }
    }
}
