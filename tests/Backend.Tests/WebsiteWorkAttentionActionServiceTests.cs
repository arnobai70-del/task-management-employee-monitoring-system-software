using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class WebsiteWorkAttentionActionServiceTests
{
    [Fact]
    public async Task Acknowledge_persists_activity_and_audit_and_suppresses_current_signal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var manager = AddManager(db, "boss@example.com", "Boss");
        var employee = AddEmployee(db, "ATT-ACT-1", "Jihad");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, new DateOnly(2026, 9, 29));
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkAttentionActionService(db, new FixedTimeProvider(now));
        var result = await service.AcknowledgeAsync(
            task.Id,
            new WebsiteWorkAttentionAcknowledgeRequest("Handled by supervisor."),
            new RequestActor(manager.Id, "127.0.0.1", "test"),
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(WebsiteWorkAttentionDisposition.Acknowledged, result.Value!.Disposition);
        Assert.Contains(db.TaskActivities, activity =>
            activity.ProjectTaskId == task.Id &&
            activity.Action == WebsiteWorkAttentionActionService.AcknowledgedAction);
        Assert.Contains(db.AuditLogs, audit =>
            audit.TargetId == task.Id.ToString() &&
            audit.Action == "website-work.attention.acknowledged");

        var attention = CreateAttentionService(db, now);
        var active = await attention.GetAsync(0, 20, cancellationToken);
        var managed = await attention.GetAsync(0, 20, true, cancellationToken);
        Assert.Empty(active.Value!.Items);
        var item = Assert.Single(managed.Value!.Items);
        Assert.True(item.Management.IsSuppressed);
        Assert.Equal("boss@example.com", item.Management.ActorEmail);
    }

    [Fact]
    public async Task Follow_up_owners_are_permission_scoped_and_assignment_is_persisted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var manager = AddManager(db, "manager@example.com", "Manager One");
        var otherUser = new User
        {
            Email = "viewer@example.com",
            NormalizedEmail = "VIEWER@EXAMPLE.COM",
            PasswordHash = "hash",
            IsActive = true
        };
        db.Users.Add(otherUser);
        var employee = AddEmployee(db, "ATT-ACT-2", "Worker");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, new DateOnly(2026, 9, 29));
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkAttentionActionService(db, new FixedTimeProvider(now));
        var owners = await service.GetFollowUpOwnersAsync(cancellationToken);
        var owner = Assert.Single(owners);
        Assert.Equal(manager.Id, owner.UserId);
        Assert.Equal("Manager One", owner.FullName);

        var invalid = await service.AssignFollowUpAsync(
            task.Id,
            new WebsiteWorkAttentionFollowUpRequest(otherUser.Id, now.AddHours(2), "Check the target."),
            new RequestActor(manager.Id, null, null),
            cancellationToken);
        Assert.Equal(OperationStatus.Invalid, invalid.Status);
        Assert.Equal("follow_up_owner_invalid", invalid.ErrorCode);

        var assigned = await service.AssignFollowUpAsync(
            task.Id,
            new WebsiteWorkAttentionFollowUpRequest(manager.Id, now.AddHours(2), "Check the target."),
            new RequestActor(manager.Id, null, null),
            cancellationToken);
        Assert.Equal(OperationStatus.Success, assigned.Status);
        Assert.Equal(WebsiteWorkAttentionDisposition.FollowUp, assigned.Value!.Disposition);
        Assert.Contains(db.TaskActivities, activity =>
            activity.ProjectTaskId == task.Id &&
            activity.Action == WebsiteWorkAttentionActionService.FollowUpAssignedAction);

        var managed = await CreateAttentionService(db, now).GetAsync(0, 20, true, cancellationToken);
        var item = Assert.Single(managed.Value!.Items);
        Assert.True(item.Management.IsSuppressed);
        Assert.Equal(manager.Id, item.Management.FollowUpOwnerUserId);
        Assert.Equal("manager@example.com", item.Management.FollowUpOwnerEmail);
        Assert.Equal("Check the target.", item.Management.Note);
        Assert.Equal(1, managed.Value.PendingFollowUpTotal);
        Assert.Equal(0, managed.Value.OverdueFollowUpTotal);
    }

    [Fact]
    public async Task My_follow_ups_are_owner_scoped_become_overdue_and_can_be_resolved_by_owner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var owner = AddManager(db, "owner@example.com", "Follow-up Owner");
        var assigningManager = AddManager(db, "assigner@example.com", "Assigning Manager");
        var employee = AddEmployee(db, "ATT-ACT-4", "Target Worker");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, new DateOnly(2026, 9, 30));
        await db.SaveChangesAsync(cancellationToken);

        var assignService = new WebsiteWorkAttentionActionService(db, new FixedTimeProvider(now));
        var assigned = await assignService.AssignFollowUpAsync(
            task.Id,
            new WebsiteWorkAttentionFollowUpRequest(owner.Id, now.AddHours(2), "Check the worker's target status."),
            new RequestActor(assigningManager.Id, "127.0.0.2", "test"),
            cancellationToken);
        Assert.Equal(OperationStatus.Success, assigned.Status);

        var ownerInbox = await assignService.GetMyFollowUpsAsync(
            new RequestActor(owner.Id, null, null),
            false,
            cancellationToken);
        Assert.Equal(OperationStatus.Success, ownerInbox.Status);
        Assert.Equal(1, ownerInbox.Value!.Pending);
        Assert.Equal(0, ownerInbox.Value.Overdue);
        var pending = Assert.Single(ownerInbox.Value.Items);
        Assert.Equal(WebsiteWorkFollowUpState.Pending, pending.State);
        Assert.Equal("assigner@example.com", pending.AssignedByEmail);
        Assert.Equal("Check the worker's target status.", pending.AssignmentNote);

        var assignerInbox = await assignService.GetMyFollowUpsAsync(
            new RequestActor(assigningManager.Id, null, null),
            false,
            cancellationToken);
        Assert.Empty(assignerInbox.Value!.Items);

        var overdueService = new WebsiteWorkAttentionActionService(db, new FixedTimeProvider(now.AddHours(3)));
        var overdueInbox = await overdueService.GetMyFollowUpsAsync(
            new RequestActor(owner.Id, null, null),
            false,
            cancellationToken);
        Assert.Equal(0, overdueInbox.Value!.Pending);
        Assert.Equal(1, overdueInbox.Value.Overdue);
        Assert.Equal(WebsiteWorkFollowUpState.Overdue, Assert.Single(overdueInbox.Value.Items).State);

        var unauthorized = await overdueService.ResolveFollowUpAsync(
            task.Id,
            new WebsiteWorkAttentionResolveFollowUpRequest(null),
            new RequestActor(assigningManager.Id, null, null),
            cancellationToken);
        Assert.Equal(OperationStatus.Conflict, unauthorized.Status);
        Assert.Equal("follow_up_not_owned", unauthorized.ErrorCode);

        var resolveService = new WebsiteWorkAttentionActionService(db, new FixedTimeProvider(now.AddHours(3).AddMinutes(1)));
        var resolved = await resolveService.ResolveFollowUpAsync(
            task.Id,
            new WebsiteWorkAttentionResolveFollowUpRequest("Worker contacted; target status verified."),
            new RequestActor(owner.Id, "127.0.0.3", "test"),
            cancellationToken);
        Assert.Equal(OperationStatus.Success, resolved.Status);
        Assert.Equal(WebsiteWorkAttentionDisposition.Resolved, resolved.Value!.Disposition);
        Assert.Contains(db.TaskActivities, activity =>
            activity.ProjectTaskId == task.Id &&
            activity.Action == WebsiteWorkAttentionActionService.FollowUpResolvedAction);
        Assert.Contains(db.AuditLogs, audit =>
            audit.TargetId == task.Id.ToString() &&
            audit.Action == "website-work.attention.follow-up-resolved");

        var pendingAfterResolve = await resolveService.GetMyFollowUpsAsync(
            new RequestActor(owner.Id, null, null),
            false,
            cancellationToken);
        Assert.Empty(pendingAfterResolve.Value!.Items);
        Assert.Equal(1, pendingAfterResolve.Value.Resolved);

        var history = await resolveService.GetMyFollowUpsAsync(
            new RequestActor(owner.Id, null, null),
            true,
            cancellationToken);
        var resolvedItem = Assert.Single(history.Value!.Items);
        Assert.Equal(WebsiteWorkFollowUpState.Resolved, resolvedItem.State);
        Assert.Equal("Worker contacted; target status verified.", resolvedItem.ResolutionNote);

        var attention = await CreateAttentionService(db, now.AddHours(3).AddMinutes(1))
            .GetAsync(0, 20, true, cancellationToken);
        var attentionItem = Assert.Single(attention.Value!.Items);
        Assert.Equal(WebsiteWorkAttentionDisposition.Resolved, attentionItem.Management.Disposition);
        Assert.True(attentionItem.Management.IsSuppressed);
        Assert.Equal(0, attention.Value.PendingFollowUpTotal);
        Assert.Equal(0, attention.Value.OverdueFollowUpTotal);
    }

    [Fact]
    public async Task Snooze_validates_duration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var manager = AddManager(db, "boss@example.com", "Boss");
        var employee = AddEmployee(db, "ATT-ACT-3", "Worker");
        var project = AddProject(db);
        var task = AddWebsiteWork(db, employee, project, new DateOnly(2026, 9, 29));
        await db.SaveChangesAsync(cancellationToken);

        var service = new WebsiteWorkAttentionActionService(db, new FixedTimeProvider(now));
        var invalid = await service.SnoozeAsync(
            task.Id,
            new WebsiteWorkAttentionSnoozeRequest(5, null),
            new RequestActor(manager.Id, null, null),
            cancellationToken);

        Assert.Equal(OperationStatus.Invalid, invalid.Status);
        Assert.Equal("snooze_minutes_invalid", invalid.ErrorCode);
    }

    private static WebsiteWorkAttentionService CreateAttentionService(AppDbContext db, DateTime now)
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
            .UseInMemoryDatabase($"website-work-attention-actions-{Guid.NewGuid():N}")
            .Options);

    private static User AddManager(AppDbContext db, string email, string fullName)
    {
        var permission = new Permission
        {
            Code = PermissionCatalog.TasksManage,
            Description = "Manage tasks"
        };
        var role = new Role
        {
            Name = $"Manager-{Guid.NewGuid():N}",
            IsActive = true
        };
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "hash",
            IsActive = true
        };
        var department = new Department
        {
            Code = $"M-{Guid.NewGuid():N}"[..12],
            NormalizedCode = $"M-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name = "Management",
            NormalizedName = "MANAGEMENT",
            IsActive = true
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            DepartmentId = department.Id,
            Department = department,
            EmployeeCode = $"MGR-{Guid.NewGuid():N}"[..12],
            NormalizedEmployeeCode = $"MGR-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            FullName = fullName,
            NormalizedFullName = fullName.ToUpperInvariant(),
            JobTitle = "Manager",
            IsActive = true
        };
        var userRole = new UserRole
        {
            UserId = user.Id,
            User = user,
            RoleId = role.Id,
            Role = role
        };
        var rolePermission = new RolePermission
        {
            RoleId = role.Id,
            Role = role,
            PermissionId = permission.Id,
            Permission = permission
        };
        user.UserRoles.Add(userRole);
        role.UserRoles.Add(userRole);
        role.RolePermissions.Add(rolePermission);
        permission.RolePermissions.Add(rolePermission);
        user.Employee = employee;
        db.Permissions.Add(permission);
        db.Roles.Add(role);
        db.Users.Add(user);
        db.Departments.Add(department);
        db.Employees.Add(employee);
        db.UserRoles.Add(userRole);
        db.RolePermissions.Add(rolePermission);
        return user;
    }

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
        user.Employee = employee;
        db.Departments.Add(department);
        db.Users.Add(user);
        db.Employees.Add(employee);
        return employee;
    }

    private static Project AddProject(AppDbContext db)
    {
        var code = $"ACT-{Guid.NewGuid():N}"[..12];
        var project = new Project
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = "Attention Actions",
            NormalizedName = "ATTENTION ACTIONS",
            Status = ProjectStatus.Active
        };
        db.Projects.Add(project);
        return project;
    }

    private static ProjectTask AddWebsiteWork(AppDbContext db, Employee employee, Project project, DateOnly dueDate)
    {
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = "InboxDollars target",
            NormalizedTitle = "INBOXDOLLARS TARGET",
            Status = ProjectTaskStatus.ToDo,
            Priority = ProjectTaskPriority.Normal,
            AssigneeEmployeeId = employee.Id,
            AssigneeEmployee = employee,
            DueDate = dueDate,
            CreatedAtUtc = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc)
        };
        task.Activities.Add(new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            ActorUserId = employee.UserId,
            Action = WebsiteWorkService.ConfiguredAction,
            DetailsJson = "{}",
            CreatedAtUtc = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc)
        });
        db.ProjectTasks.Add(task);
        return task;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
