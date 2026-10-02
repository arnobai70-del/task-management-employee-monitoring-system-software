using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AuditRound5RegressionTests
{
    private static readonly DateTime UtcNow = DateTime.Parse("2026-10-02T06:00:00Z").ToUniversalTime();

    [Fact]
    public async Task Employee_my_tasks_excludes_website_work_that_has_its_own_workspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (user, employee) = AddEmployee(db, "R5-TASK", "Round Five Worker");
        var project = AddProject(db);

        var ordinaryTask = AddTask(db, project, employee, "Ordinary project task");
        var websiteWork = AddTask(db, project, employee, "Website work target");
        db.TaskActivities.Add(new TaskActivity
        {
            ProjectTaskId = websiteWork.Id,
            ProjectTask = websiteWork,
            Action = WebsiteWorkService.ConfiguredAction,
            DetailsJson = "{\"url\":\"https://example.com/work\"}",
            CreatedAtUtc = UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db);
        var result = await service.GetMyTasksAsync(
            new RequestActor(user.Id, "127.0.0.1", "tests"),
            includeClosed: true,
            page: 1,
            pageSize: 100,
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(1, result.Value!.TotalCount);
        var visible = Assert.Single(result.Value.Items);
        Assert.Equal(ordinaryTask.Id, visible.Id);
        Assert.DoesNotContain(result.Value.Items, item => item.Id == websiteWork.Id);
    }

    [Fact]
    public async Task Employee_workspace_rejects_inactive_linked_user_even_when_employee_row_is_active()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (user, employee) = AddEmployee(db, "R5-OFF", "Inactive Account Worker");
        user.IsActive = false;
        employee.IsActive = true;
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db);
        var actor = new RequestActor(user.Id, "127.0.0.1", "tests");

        var tasks = await service.GetMyTasksAsync(actor, false, 1, 50, cancellationToken);
        Assert.Equal(OperationStatus.Invalid, tasks.Status);
        Assert.Equal("employee_inactive", tasks.ErrorCode);

        var access = await service.GetMyAccessAsync(actor, false, cancellationToken);
        Assert.Equal(OperationStatus.Invalid, access.Status);
        Assert.Equal("employee_inactive", access.ErrorCode);
    }

    private static EmployeeWorkspaceService CreateService(AppDbContext db)
        => new(db, new FixedTimeProvider(new DateTimeOffset(UtcNow)));

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
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

    private static Project AddProject(AppDbContext db)
    {
        var project = new Project
        {
            Code = "R5-PROJECT",
            NormalizedCode = "R5-PROJECT",
            Name = "Round Five Project",
            NormalizedName = "ROUND FIVE PROJECT",
            Status = ProjectStatus.Active,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectTask AddTask(AppDbContext db, Project project, Employee employee, string title)
    {
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = title,
            NormalizedTitle = title.ToUpperInvariant(),
            Status = ProjectTaskStatus.InProgress,
            Priority = ProjectTaskPriority.Normal,
            AssigneeEmployeeId = employee.Id,
            AssigneeEmployee = employee,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        db.ProjectTasks.Add(task);
        return task;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
