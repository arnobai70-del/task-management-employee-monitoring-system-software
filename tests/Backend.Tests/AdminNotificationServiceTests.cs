using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AdminNotificationServiceTests
{
    [Fact]
    public async Task Notification_center_tracks_unread_and_read_state_durably()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = Utc(9);
        await using var db = CreateDb();
        var manager = AddUser(db, "manager@example.com");
        var task = AddTask(db);
        var source = AddActivity(db, task, WebsiteWorkAttentionActionService.FollowUpAssignedAction, Utc(8));
        var notification = AdminNotificationService.CreateActivity(
            task,
            manager.Id,
            AdminNotificationKind.FollowUpAssigned,
            "New follow-up assigned",
            "Worker: Website target",
            source.Id,
            now,
            Utc(12));
        db.TaskActivities.Add(notification);
        await db.SaveChangesAsync(cancellationToken);

        var service = new AdminNotificationService(db, new MutableTimeProvider(now));
        var actor = new RequestActor(manager.Id, "127.0.0.1", "test");
        var summary = await service.GetSummaryAsync(actor, cancellationToken);
        Assert.Equal(OperationStatus.Success, summary.Status);
        Assert.Equal(1, summary.Value!.Unread);
        Assert.Equal(1, summary.Value.Total);

        var unread = await service.GetMineAsync(actor, true, 1, 50, cancellationToken);
        var item = Assert.Single(unread.Value!.Items);
        Assert.Null(item.ReadAtUtc);
        Assert.Equal(AdminNotificationKind.FollowUpAssigned, item.Kind);

        var read = await service.MarkReadAsync(actor, item.Id, cancellationToken);
        Assert.Equal(OperationStatus.Success, read.Status);
        Assert.NotNull(read.Value!.ReadAtUtc);

        summary = await service.GetSummaryAsync(actor, cancellationToken);
        Assert.Equal(0, summary.Value!.Unread);
        Assert.Equal(1, summary.Value.Total);
        Assert.Empty((await service.GetMineAsync(actor, true, 1, 50, cancellationToken)).Value!.Items);
    }

    [Fact]
    public async Task Reminder_scan_creates_due_soon_and_overdue_once_for_current_assignment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new MutableTimeProvider(Utc(9));
        await using var db = CreateDb();
        var manager = AddUser(db, "manager@example.com");
        var task = AddTask(db);
        AddActivity(db, task, WebsiteWorkService.ConfiguredAction, Utc(8));
        var followUp = AddActivity(db, task, WebsiteWorkAttentionActionService.FollowUpAssignedAction, Utc(8, 30), new
        {
            actorUserId = Guid.NewGuid(),
            actorEmail = "boss@example.com",
            note = "Check target.",
            followUpOwnerUserId = manager.Id,
            followUpOwnerEmail = manager.Email,
            followUpOwnerName = "Manager",
            followUpDueAtUtc = Utc(9, 20)
        });
        await db.SaveChangesAsync(cancellationToken);

        var reminder = new FollowUpReminderService(
            db,
            clock,
            Options.Create(new FollowUpReminderOptions
            {
                DueSoonMinutes = 30,
                EscalationAfterMinutes = 60,
                ScanIntervalSeconds = 60
            }));

        Assert.Equal(1, await reminder.ScanAsync(cancellationToken));
        Assert.Equal(0, await reminder.ScanAsync(cancellationToken));
        var dueSoon = Assert.Single(db.TaskActivities.Where(activity => AdminNotificationService.IsNotificationAction(activity.Action)));
        var dueSoonResponse = AdminNotificationService.ToResponse(dueSoon);
        Assert.Equal(AdminNotificationKind.FollowUpDueSoon, dueSoonResponse!.Kind);

        clock.UtcNow = Utc(9, 21);
        Assert.Equal(1, await reminder.ScanAsync(cancellationToken));
        Assert.Equal(0, await reminder.ScanAsync(cancellationToken));

        var notifications = db.TaskActivities
            .Where(activity => AdminNotificationService.IsNotificationAction(activity.Action))
            .AsEnumerable()
            .Select(AdminNotificationService.ToResponse)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        Assert.Equal(2, notifications.Length);
        Assert.Contains(notifications, item => item.Kind == AdminNotificationKind.FollowUpDueSoon);
        Assert.Contains(notifications, item => item.Kind == AdminNotificationKind.FollowUpOverdue);
        Assert.All(notifications, item => Assert.Equal(task.Id, item.TaskId));
        Assert.Equal(followUp.Id, ReadSourceActivityId(dueSoon.DetailsJson));
    }

    [Fact]
    public async Task Reminder_scan_escalates_to_the_follow_up_owners_supervisor_once_after_grace_period()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new MutableTimeProvider(Utc(10));
        await using var db = CreateDb();
        var boss = AddUser(db, "boss@example.com");
        GrantTaskManagement(db, boss, "TeamLead");
        var bossEmployee = AddEmployeeProfile(db, boss, "BOSS-1", "Boss");
        var manager = AddUser(db, "manager@example.com");
        AddEmployeeProfile(db, manager, "MGR-1", "Manager One", bossEmployee);
        var worker = AddUser(db, "worker@example.com");
        var workerEmployee = AddEmployeeProfile(db, worker, "WRK-1", "Jihad");
        var task = AddTask(db);
        task.AssigneeEmployee = workerEmployee;
        task.AssigneeEmployeeId = workerEmployee.Id;
        AddActivity(db, task, WebsiteWorkService.ConfiguredAction, Utc(8));
        var followUp = AddActivity(db, task, WebsiteWorkAttentionActionService.FollowUpAssignedAction, Utc(8, 30), new
        {
            actorUserId = Guid.NewGuid(),
            actorEmail = "admin@example.com",
            note = "Check target.",
            followUpOwnerUserId = manager.Id,
            followUpOwnerEmail = manager.Email,
            followUpOwnerName = "Manager One",
            followUpDueAtUtc = Utc(9)
        });
        await db.SaveChangesAsync(cancellationToken);

        var reminder = new FollowUpReminderService(
            db,
            clock,
            Options.Create(new FollowUpReminderOptions
            {
                DueSoonMinutes = 30,
                EscalationAfterMinutes = 60,
                EscalationFallbackRoles = ["SuperAdmin", "Admin"],
                ScanIntervalSeconds = 60
            }));

        Assert.Equal(2, await reminder.ScanAsync(cancellationToken));
        Assert.Equal(0, await reminder.ScanAsync(cancellationToken));

        var notifications = db.TaskActivities
            .Where(activity => AdminNotificationService.IsNotificationAction(activity.Action))
            .AsEnumerable()
            .Select(activity => new { Activity = activity, Response = AdminNotificationService.ToResponse(activity)! })
            .ToArray();
        Assert.Contains(notifications, item =>
            item.Activity.ActorUserId == manager.Id &&
            item.Response.Kind == AdminNotificationKind.FollowUpOverdue);
        var escalation = Assert.Single(notifications, item =>
            item.Activity.ActorUserId == boss.Id &&
            item.Response.Kind == AdminNotificationKind.FollowUpEscalated);
        Assert.Equal("/website-work", escalation.Response.ActionUrl);
        Assert.Contains("Manager One", escalation.Response.Message);
        Assert.Contains("Jihad", escalation.Response.Message);
        Assert.Equal(followUp.Id, ReadSourceActivityId(escalation.Activity.DetailsJson));
    }

    [Fact]
    public async Task Reminder_scan_uses_configured_boss_role_when_owner_has_no_reporting_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new MutableTimeProvider(Utc(11));
        await using var db = CreateDb();
        var executive = AddUser(db, "executive@example.com");
        GrantTaskManagement(db, executive, "OperationsBoss");
        var manager = AddUser(db, "manager@example.com");
        var task = AddTask(db);
        AddActivity(db, task, WebsiteWorkService.ConfiguredAction, Utc(8));
        AddActivity(db, task, WebsiteWorkAttentionActionService.FollowUpAssignedAction, Utc(8, 30), new
        {
            actorUserId = Guid.NewGuid(),
            actorEmail = "admin@example.com",
            note = "Check target.",
            followUpOwnerUserId = manager.Id,
            followUpOwnerEmail = manager.Email,
            followUpOwnerName = "Manager",
            followUpDueAtUtc = Utc(9)
        });
        await db.SaveChangesAsync(cancellationToken);

        var reminder = new FollowUpReminderService(
            db,
            clock,
            Options.Create(new FollowUpReminderOptions
            {
                DueSoonMinutes = 30,
                EscalationAfterMinutes = 60,
                EscalationFallbackRoles = ["OperationsBoss"],
                ScanIntervalSeconds = 60
            }));

        Assert.Equal(2, await reminder.ScanAsync(cancellationToken));
        Assert.Contains(db.TaskActivities.AsEnumerable(), activity =>
            activity.ActorUserId == executive.Id &&
            AdminNotificationService.ToResponse(activity)?.Kind == AdminNotificationKind.FollowUpEscalated);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"admin-notifications-{Guid.NewGuid():N}")
            .Options);

    private static User AddUser(AppDbContext db, string email)
    {
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "hash",
            IsActive = true
        };
        db.Users.Add(user);
        return user;
    }

    private static Employee AddEmployeeProfile(
        AppDbContext db,
        User user,
        string code,
        string fullName,
        Employee? supervisor = null)
    {
        var employee = new Employee
        {
            User = user,
            UserId = user.Id,
            Supervisor = supervisor,
            SupervisorEmployeeId = supervisor?.Id,
            EmployeeCode = code,
            NormalizedEmployeeCode = code.ToUpperInvariant(),
            FullName = fullName,
            NormalizedFullName = fullName.ToUpperInvariant(),
            JobTitle = "Manager",
            IsActive = true
        };
        user.Employee = employee;
        supervisor?.DirectReports.Add(employee);
        db.Employees.Add(employee);
        return employee;
    }

    private static void GrantTaskManagement(AppDbContext db, User user, string roleName)
    {
        var tasksRead = new Permission
        {
            Code = PermissionCatalog.TasksRead,
            Description = "Read tasks"
        };
        var tasksManage = new Permission
        {
            Code = PermissionCatalog.TasksManage,
            Description = "Manage tasks"
        };
        var role = new Role
        {
            Name = roleName,
            IsActive = true
        };
        var userRole = new UserRole
        {
            User = user,
            UserId = user.Id,
            Role = role,
            RoleId = role.Id
        };
        var readPermission = new RolePermission
        {
            Role = role,
            RoleId = role.Id,
            Permission = tasksRead,
            PermissionId = tasksRead.Id
        };
        var managePermission = new RolePermission
        {
            Role = role,
            RoleId = role.Id,
            Permission = tasksManage,
            PermissionId = tasksManage.Id
        };
        user.UserRoles.Add(userRole);
        role.UserRoles.Add(userRole);
        role.RolePermissions.Add(readPermission);
        role.RolePermissions.Add(managePermission);
        tasksRead.RolePermissions.Add(readPermission);
        tasksManage.RolePermissions.Add(managePermission);
        db.Permissions.AddRange(tasksRead, tasksManage);
        db.Roles.Add(role);
        db.UserRoles.Add(userRole);
        db.RolePermissions.AddRange(readPermission, managePermission);
    }

    private static ProjectTask AddTask(AppDbContext db)
    {
        var code = $"NT-{Guid.NewGuid():N}"[..12];
        var project = new Project
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = "Notification Tests",
            NormalizedName = "NOTIFICATION TESTS",
            Status = ProjectStatus.Active
        };
        var task = new ProjectTask
        {
            Project = project,
            ProjectId = project.Id,
            Title = "Website target",
            NormalizedTitle = "WEBSITE TARGET",
            Status = ProjectTaskStatus.ToDo,
            Priority = ProjectTaskPriority.Normal,
            CreatedAtUtc = Utc(8),
            UpdatedAtUtc = Utc(8)
        };
        db.Projects.Add(project);
        db.ProjectTasks.Add(task);
        return task;
    }

    private static TaskActivity AddActivity(
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
        return activity;
    }

    private static Guid ReadSourceActivityId(string detailsJson)
    {
        using var document = JsonDocument.Parse(detailsJson);
        return document.RootElement.GetProperty("sourceActivityId").GetGuid();
    }

    private static DateTime Utc(int hour, int minute = 0)
        => new(2026, 10, 1, hour, minute, 0, DateTimeKind.Utc);

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }
}
