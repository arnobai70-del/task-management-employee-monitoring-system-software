using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class WebsiteWorkProgressServiceTests
{
    [Fact]
    public async Task Progress_reports_current_work_duration_and_local_day_completion_counts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 4, 30, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var jihad = AddEmployee(db, "WEB-101", "Jihad");
        var nila = AddEmployee(db, "WEB-102", "Nila");
        var project = AddProject(db);

        AddWebsiteWork(db, project, jihad, "Current target", ProjectTaskStatus.InProgress,
            startedAtUtc: new DateTime(2026, 9, 30, 3, 0, 0, DateTimeKind.Utc));
        AddWebsiteWork(db, project, jihad, "Completed this morning", ProjectTaskStatus.Done,
            completedAtUtc: new DateTime(2026, 9, 30, 2, 30, 0, DateTimeKind.Utc));
        AddWebsiteWork(db, project, nila, "Completed just after local midnight", ProjectTaskStatus.Done,
            completedAtUtc: new DateTime(2026, 9, 29, 18, 10, 0, DateTimeKind.Utc));
        AddWebsiteWork(db, project, nila, "Previous local day", ProjectTaskStatus.Done,
            completedAtUtc: new DateTime(2026, 9, 29, 17, 50, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkProgressService(db, new FixedTimeProvider(now));
        var snapshot = await service.GetAsync(360, cancellationToken);

        Assert.Equal(now, snapshot.GeneratedAtUtc);
        Assert.Equal(360, snapshot.UtcOffsetMinutes);
        Assert.Equal(1, snapshot.WorkingNow);
        Assert.Equal(2, snapshot.CompletedToday);

        var active = Assert.Single(snapshot.ActiveWork);
        Assert.Equal(jihad.Id, active.EmployeeId);
        Assert.Equal("Current target", active.TaskTitle);
        Assert.Equal(new DateTime(2026, 9, 30, 3, 0, 0, DateTimeKind.Utc), active.StartedAtUtc);
        Assert.Equal(5_400L, active.ElapsedSeconds);

        Assert.Collection(snapshot.Employees,
            employee =>
            {
                Assert.Equal(jihad.Id, employee.EmployeeId);
                Assert.Equal(1, employee.WorkingNow);
                Assert.Equal(1, employee.CompletedToday);
                Assert.Equal(new DateTime(2026, 9, 30, 2, 30, 0, DateTimeKind.Utc), employee.LastCompletedAtUtc);
            },
            employee =>
            {
                Assert.Equal(nila.Id, employee.EmployeeId);
                Assert.Equal(0, employee.WorkingNow);
                Assert.Equal(1, employee.CompletedToday);
                Assert.Equal(new DateTime(2026, 9, 29, 18, 10, 0, DateTimeKind.Utc), employee.LastCompletedAtUtc);
            });
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"website-work-progress-{Guid.NewGuid():N}")
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

    private static Project AddProject(AppDbContext db)
    {
        var project = new Project
        {
            Code = "LIVE-WEB",
            NormalizedCode = "LIVE-WEB",
            Name = "Live Website Targets",
            NormalizedName = "LIVE WEBSITE TARGETS",
            Status = ProjectStatus.Active
        };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectTask AddWebsiteWork(
        AppDbContext db,
        Project project,
        Employee employee,
        string title,
        ProjectTaskStatus status,
        DateTime? startedAtUtc = null,
        DateTime? completedAtUtc = null)
    {
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = title,
            NormalizedTitle = title.ToUpperInvariant(),
            Status = status,
            Priority = ProjectTaskPriority.Normal,
            AssigneeEmployeeId = employee.Id,
            AssigneeEmployee = employee,
            CompletedAtUtc = completedAtUtc,
            CreatedAtUtc = startedAtUtc ?? completedAtUtc ?? DateTime.UtcNow,
            UpdatedAtUtc = startedAtUtc ?? completedAtUtc ?? DateTime.UtcNow
        };
        db.ProjectTasks.Add(task);

        AddActivity(db, task, WebsiteWorkService.ConfiguredAction, task.CreatedAtUtc);
        if (startedAtUtc.HasValue)
        {
            AddActivity(db, task, WebsiteWorkService.StartedAction, startedAtUtc.Value);
        }
        if (completedAtUtc.HasValue)
        {
            AddActivity(db, task, WebsiteWorkService.CompletedAction, completedAtUtc.Value);
        }

        return task;
    }

    private static void AddActivity(AppDbContext db, ProjectTask task, string action, DateTime createdAtUtc)
    {
        var activity = new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            Action = action,
            DetailsJson = "{}",
            CreatedAtUtc = createdAtUtc
        };
        db.TaskActivities.Add(activity);
        task.Activities.Add(activity);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
