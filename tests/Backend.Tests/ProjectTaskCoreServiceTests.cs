using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class ProjectTaskCoreServiceTests
{
    [Fact]
    public async Task Duplicate_project_code_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var service = Service(db);
        var request = new CreateProjectRequest { Code = "OPS", Name = "Operations", Status = ProjectStatus.Active };

        var first = await service.CreateProjectAsync(request, Actor(), cancellationToken);
        var duplicate = await service.CreateProjectAsync(new CreateProjectRequest { Code = " ops ", Name = "Other", Status = ProjectStatus.Active }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Success, first.Status);
        Assert.Equal(OperationStatus.Conflict, duplicate.Status);
        Assert.Equal("project_code_exists", duplicate.ErrorCode);
    }

    [Fact]
    public async Task Task_assignee_must_be_active_project_member()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (_, employee) = await AddEmployeeAsync(db, cancellationToken);
        var project = AddProject(db);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);

        var result = await service.CreateTaskAsync(new CreateProjectTaskRequest
        {
            ProjectId = project.Id,
            Title = "Restricted assignment",
            AssigneeEmployeeId = employee.Id
        }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("assignee_not_project_member", result.ErrorCode);
    }

    [Fact]
    public async Task Member_with_open_assigned_task_cannot_be_removed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (_, employee) = await AddEmployeeAsync(db, cancellationToken);
        var project = AddProject(db);
        AddMember(db, project, employee);
        AddTask(db, project, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);

        var result = await service.RemoveProjectMemberAsync(project.Id, employee.Id, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Conflict, result.Status);
        Assert.Equal("project_member_has_open_tasks", result.ErrorCode);
    }

    [Fact]
    public async Task Invalid_task_status_jump_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var task = AddTask(db, project);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);

        var result = await service.ChangeTaskStatusAsync(task.Id, new ChangeProjectTaskStatusRequest { Status = ProjectTaskStatus.Done }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Conflict, result.Status);
        Assert.Equal("task_status_transition_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Valid_task_completion_sets_timestamp_and_records_activity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (user, _) = await AddEmployeeAsync(db, cancellationToken);
        var project = AddProject(db);
        var task = AddTask(db, project);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);
        var actor = Actor(user.Id);

        var started = await service.ChangeTaskStatusAsync(task.Id, new ChangeProjectTaskStatusRequest { Status = ProjectTaskStatus.InProgress }, actor, cancellationToken);
        var completed = await service.ChangeTaskStatusAsync(task.Id, new ChangeProjectTaskStatusRequest { Status = ProjectTaskStatus.Done }, actor, cancellationToken);

        Assert.Equal(OperationStatus.Success, started.Status);
        Assert.Equal(OperationStatus.Success, completed.Status);
        Assert.NotNull(completed.Value);
        Assert.Equal(ProjectTaskStatus.Done, completed.Value.Status);
        Assert.NotNull(completed.Value.CompletedAtUtc);
        Assert.Equal(2, await db.TaskActivities.CountAsync(x => x.ProjectTaskId == task.Id && x.Action == "task.status.changed", cancellationToken));
    }

    [Fact]
    public async Task Project_cannot_complete_while_open_tasks_exist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        AddTask(db, project);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);

        var result = await service.UpdateProjectAsync(project.Id, new UpdateProjectRequest
        {
            Code = project.Code,
            Name = project.Name,
            Status = ProjectStatus.Completed,
            StartDate = project.StartDate,
            DueDate = project.DueDate
        }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Conflict, result.Status);
        Assert.Equal("project_has_open_tasks", result.ErrorCode);
    }

    [Fact]
    public async Task Task_due_date_must_stay_inside_project_dates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);

        var result = await service.CreateTaskAsync(new CreateProjectTaskRequest
        {
            ProjectId = project.Id,
            Title = "Late task",
            DueDate = new DateOnly(2026, 11, 1)
        }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("task_due_after_project_due", result.ErrorCode);
    }

    [Fact]
    public async Task Adding_comment_creates_comment_activity_and_audit_records()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var (user, _) = await AddEmployeeAsync(db, cancellationToken);
        var project = AddProject(db);
        var task = AddTask(db, project);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);

        var result = await service.AddCommentAsync(task.Id, new CreateTaskCommentRequest { Body = "  Ready for review.  " }, Actor(user.Id), cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal("Ready for review.", result.Value.Body);
        Assert.Equal(1, await db.TaskComments.CountAsync(x => x.ProjectTaskId == task.Id, cancellationToken));
        Assert.Equal(1, await db.TaskActivities.CountAsync(x => x.ProjectTaskId == task.Id && x.Action == "task.comment.added", cancellationToken));
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.Action == "task.comment.added", cancellationToken));
    }

    [Fact]
    public async Task Project_summary_counts_only_active_members_and_open_tasks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateDbOptions();
        Guid projectId;

        await using (var seedDb = new AppDbContext(options))
        {
            var (_, activeEmployee) = await AddEmployeeAsync(seedDb, cancellationToken);
            var (_, inactiveEmployee) = await AddEmployeeAsync(seedDb, cancellationToken);
            var project = AddProject(seedDb);
            projectId = project.Id;
            AddMember(seedDb, project, activeEmployee);
            var inactiveMember = AddMember(seedDb, project, inactiveEmployee);
            inactiveMember.IsActive = false;
            inactiveMember.RemovedAtUtc = UtcNow;
            AddTask(seedDb, project);
            AddTask(seedDb, project, status: ProjectTaskStatus.Done);
            await seedDb.SaveChangesAsync(cancellationToken);
        }

        await using var queryDb = new AppDbContext(options);
        var result = await Service(queryDb).GetProjectAsync(projectId, cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value.ActiveMemberCount);
        Assert.Equal(1, result.Value.OpenTaskCount);
    }

    private static DateTime UtcNow => DateTime.Parse("2026-09-29T06:00:00Z").ToUniversalTime();

    private static DbContextOptions<AppDbContext> CreateDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static AppDbContext CreateDbContext() => new(CreateDbOptions());

    private static ProjectTaskCoreService Service(AppDbContext db) =>
        new(db, new FixedTimeProvider(new DateTimeOffset(UtcNow)));

    private static async Task<(User User, Employee Employee)> AddEmployeeAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Email = $"project-{id}@example.com",
            NormalizedEmail = $"PROJECT-{id}@EXAMPLE.COM",
            PasswordHash = "test-hash",
            IsActive = true
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = $"P-{id}"[..12],
            NormalizedEmployeeCode = $"P-{id}"[..12].ToUpperInvariant(),
            FullName = $"Project Employee {id[..8]}",
            NormalizedFullName = $"PROJECT EMPLOYEE {id[..8]}".ToUpperInvariant(),
            JobTitle = "Project Tester",
            IsActive = true
        };
        db.Users.Add(user);
        db.Employees.Add(employee);
        await db.SaveChangesAsync(cancellationToken);
        return (user, employee);
    }

    private static Project AddProject(AppDbContext db)
    {
        var project = new Project
        {
            Code = $"PRJ-{Guid.NewGuid():N}"[..12],
            Name = "Project Core Test",
            Status = ProjectStatus.Active,
            StartDate = new DateOnly(2026, 9, 1),
            DueDate = new DateOnly(2026, 10, 31)
        };
        project.NormalizedCode = project.Code.ToUpperInvariant();
        project.NormalizedName = project.Name.ToUpperInvariant();
        db.Projects.Add(project);
        return project;
    }

    private static ProjectMember AddMember(AppDbContext db, Project project, Employee employee)
    {
        var member = new ProjectMember
        {
            ProjectId = project.Id,
            Project = project,
            EmployeeId = employee.Id,
            Employee = employee,
            Role = ProjectMemberRole.Member,
            IsActive = true,
            AddedAtUtc = UtcNow
        };
        db.ProjectMembers.Add(member);
        return member;
    }

    private static ProjectTask AddTask(AppDbContext db, Project project, Employee? assignee = null, ProjectTaskStatus status = ProjectTaskStatus.ToDo)
    {
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = $"Task {Guid.NewGuid():N}"[..18],
            Description = "Project core test task",
            Status = status,
            Priority = ProjectTaskPriority.Normal,
            AssigneeEmployeeId = assignee?.Id,
            AssigneeEmployee = assignee,
            DueDate = new DateOnly(2026, 10, 1),
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow,
            CompletedAtUtc = status == ProjectTaskStatus.Done ? UtcNow : null
        };
        task.NormalizedTitle = task.Title.ToUpperInvariant();
        db.ProjectTasks.Add(task);
        return task;
    }

    private static RequestActor Actor(Guid? userId = null) => new(userId, "127.0.0.1", "tests");

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
