using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class WebsiteWorkServiceTests
{
    [Fact]
    public async Task Assigned_employee_starts_and_completes_website_work_and_manager_event_is_published()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 4, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-001", "Jihad");
        var project = AddProject(db, employee);
        await db.SaveChangesAsync(cancellationToken);

        var publisher = new CapturePublisher();
        var service = new WebsiteWorkService(db, new FixedTimeProvider(now), publisher, NullLogger<WebsiteWorkService>.Instance);
        var actor = new RequestActor(employee.UserId, "127.0.0.1", "tests");

        var created = await service.CreateAsync(new UpsertWebsiteWorkRequest
        {
            ProjectId = project.Id,
            EmployeeId = employee.Id,
            Title = "InboxDollars - earn $5",
            Instructions = "Open the assigned site and finish the $5 target.",
            Url = "https://www.inboxdollars.example/work?id=5",
            Priority = ProjectTaskPriority.High,
            DueDate = new DateOnly(2026, 10, 1)
        }, actor, cancellationToken);

        Assert.Equal(OperationStatus.Success, created.Status);
        Assert.Equal(ProjectTaskStatus.ToDo, created.Value!.Status);
        Assert.Equal("https://www.inboxdollars.example/work?id=5", created.Value.Url);

        var mine = await service.GetMineAsync(actor, false, cancellationToken);
        Assert.Single(mine.Value!);

        var started = await service.StartAsync(created.Value.Id, actor, cancellationToken);
        Assert.Equal(OperationStatus.Success, started.Status);
        Assert.Equal(ProjectTaskStatus.InProgress, started.Value!.Status);
        Assert.NotNull(started.Value.StartedAtUtc);

        var completed = await service.CompleteAsync(created.Value.Id, actor, cancellationToken);
        Assert.Equal(OperationStatus.Success, completed.Status);
        Assert.Equal(ProjectTaskStatus.Done, completed.Value!.Status);
        Assert.NotNull(completed.Value.CompletedAtUtc);

        var notification = Assert.Single(publisher.Completions);
        Assert.Equal(employee.Id, notification.EmployeeId);
        Assert.Equal("Jihad completed: InboxDollars - earn $5", notification.Message);

        var recent = await service.GetRecentCompletionsAsync(1, 20, cancellationToken);
        var durable = Assert.Single(recent.Items);
        Assert.Equal(notification.TaskId, durable.TaskId);
        Assert.Equal(notification.Message, durable.Message);
    }

    [Fact]
    public async Task Employee_cannot_start_another_employees_website_work()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var first = AddEmployee(db, "WEB-002", "First Worker");
        var second = AddEmployee(db, "WEB-003", "Second Worker");
        var project = AddProject(db, first, second);
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkService(db, TimeProvider.System, new CapturePublisher(), NullLogger<WebsiteWorkService>.Instance);
        var created = await service.CreateAsync(new UpsertWebsiteWorkRequest
        {
            ProjectId = project.Id,
            EmployeeId = first.Id,
            Title = "Assigned target",
            Url = "https://work.example.com/target"
        }, new RequestActor(first.UserId, null, "tests"), cancellationToken);

        var result = await service.StartAsync(created.Value!.Id, new RequestActor(second.UserId, null, "tests"), cancellationToken);
        Assert.Equal(OperationStatus.NotFound, result.Status);
        Assert.Equal("website_work_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task Website_work_rejects_urls_with_embedded_credentials()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDb();
        var employee = AddEmployee(db, "WEB-004", "Safe Worker");
        var project = AddProject(db, employee);
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkService(db, TimeProvider.System, new CapturePublisher(), NullLogger<WebsiteWorkService>.Instance);
        var result = await service.CreateAsync(new UpsertWebsiteWorkRequest
        {
            ProjectId = project.Id,
            EmployeeId = employee.Id,
            Title = "Unsafe target",
            Url = "https://user:password@example.com/work"
        }, new RequestActor(employee.UserId, null, "tests"), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("website_work_url_invalid", result.ErrorCode);
        Assert.Empty(db.ProjectTasks);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"website-work-{Guid.NewGuid():N}")
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

    private static Project AddProject(AppDbContext db, params Employee[] employees)
    {
        var project = new Project
        {
            Code = $"WEB-{Guid.NewGuid():N}"[..12],
            NormalizedCode = $"WEB-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Website Targets",
            NormalizedName = "WEBSITE TARGETS",
            Status = ProjectStatus.Active,
            StartDate = new DateOnly(2026, 9, 1),
            DueDate = new DateOnly(2026, 12, 31)
        };
        db.Projects.Add(project);
        foreach (var employee in employees)
        {
            db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = project.Id,
                Project = project,
                EmployeeId = employee.Id,
                Employee = employee,
                IsActive = true,
                Role = ProjectMemberRole.Member
            });
        }
        return project;
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

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
