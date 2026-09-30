using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class FollowUpSlaAnalyticsServiceTests
{
    [Fact]
    public async Task Analytics_tracks_manager_sla_escalations_and_department_hotspots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = Utc(12);
        await using var db = CreateDb();
        var department = new Department
        {
            Code = "OPS",
            NormalizedCode = "OPS",
            Name = "Operations",
            NormalizedName = "OPERATIONS"
        };
        var manager = AddUserWithEmployee(db, "manager@example.com", "Manager One", "M-001", department);
        var boss = AddUser(db, "boss@example.com");

        var onTimeTask = AddTask(db, "On time");
        AddActivity(db, onTimeTask, WebsiteWorkService.ConfiguredAction, Utc(7));
        AddFollowUp(db, onTimeTask, manager, Utc(8), Utc(10));
        AddResolved(db, onTimeTask, manager, Utc(9, 30), Utc(10));

        var lateTask = AddTask(db, "Late resolution");
        AddActivity(db, lateTask, WebsiteWorkService.ConfiguredAction, Utc(7));
        AddFollowUp(db, lateTask, manager, Utc(8), Utc(9));
        AddResolved(db, lateTask, manager, Utc(9, 20), Utc(9));

        var escalatedTaskOne = AddTask(db, "Escalated one");
        AddActivity(db, escalatedTaskOne, WebsiteWorkService.ConfiguredAction, Utc(6));
        var escalatedOne = AddFollowUp(db, escalatedTaskOne, manager, Utc(7), Utc(8));
        db.TaskActivities.Add(AdminNotificationService.CreateActivity(
            escalatedTaskOne,
            boss.Id,
            AdminNotificationKind.FollowUpEscalated,
            "Follow-up escalated",
            "Manager missed SLA.",
            escalatedOne.Id,
            Utc(9),
            Utc(8),
            "/website-work"));

        var escalatedTaskTwo = AddTask(db, "Escalated two");
        AddActivity(db, escalatedTaskTwo, WebsiteWorkService.ConfiguredAction, Utc(5));
        var escalatedTwo = AddFollowUp(db, escalatedTaskTwo, manager, Utc(6), Utc(7));
        db.TaskActivities.Add(AdminNotificationService.CreateActivity(
            escalatedTaskTwo,
            boss.Id,
            AdminNotificationKind.FollowUpEscalated,
            "Follow-up escalated",
            "Manager missed SLA again.",
            escalatedTwo.Id,
            Utc(8),
            Utc(7),
            "/website-work"));

        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db, now);
        var result = await service.GetAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1),
            0,
            FollowUpSlaGrouping.Day,
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        var report = Assert.IsType<FollowUpSlaAnalyticsResponse>(result.Value);
        Assert.Equal(4, report.Summary.Due);
        Assert.Equal(2, report.Summary.Resolved);
        Assert.Equal(1, report.Summary.SlaMet);
        Assert.Equal(3, report.Summary.SlaBreached);
        Assert.Equal(2, report.Summary.Escalated);
        Assert.Equal(2, report.Summary.OpenOverdue);
        Assert.Equal(85m, report.Summary.AverageResolutionMinutes);
        Assert.Equal(20m, report.Summary.AverageOverdueMinutes);
        Assert.Equal(25m, report.Summary.SlaMetPercent);
        Assert.Equal(1, report.RepeatedEscalationManagers);
        Assert.Equal(60, report.EscalationAfterMinutes);

        var managerRow = Assert.Single(report.Managers);
        Assert.Equal(manager.Id, managerRow.OwnerUserId);
        Assert.Equal("Manager One", managerRow.OwnerName);
        Assert.Equal("Operations", managerRow.DepartmentName);
        Assert.True(managerRow.RepeatedEscalation);
        Assert.Equal(2, managerRow.Metrics.Escalated);

        var departmentRow = Assert.Single(report.Departments);
        Assert.Equal(department.Id, departmentRow.DepartmentId);
        Assert.Equal("Operations", departmentRow.DepartmentName);
        Assert.Equal(1, departmentRow.Managers);
        Assert.Equal(1, departmentRow.RepeatedEscalationManagers);
        Assert.Equal(3, departmentRow.Metrics.SlaBreached);

        var period = Assert.Single(report.Periods);
        Assert.Equal(4, period.Metrics.Due);
    }

    [Fact]
    public async Task Analytics_uses_local_day_boundary_and_excludes_invalidated_assignment_before_due()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = Utc(1);
        await using var db = CreateDb();
        var manager = AddUser(db, "manager@example.com");

        var excludedTask = AddTask(db, "Cancelled follow-up");
        AddActivity(db, excludedTask, WebsiteWorkService.ConfiguredAction, PreviousDayUtc(17));
        AddFollowUp(db, excludedTask, manager, PreviousDayUtc(18), PreviousDayUtc(19));
        AddActivity(db, excludedTask, WebsiteWorkAttentionActionService.SnoozedAction, PreviousDayUtc(18, 30), new
        {
            actorUserId = manager.Id,
            actorEmail = manager.Email,
            snoozedUntilUtc = Utc(2)
        });

        var includedTask = AddTask(db, "Early resolution");
        AddActivity(db, includedTask, WebsiteWorkService.ConfiguredAction, PreviousDayUtc(17));
        AddFollowUp(db, includedTask, manager, PreviousDayUtc(18), PreviousDayUtc(18, 30));
        AddResolved(db, includedTask, manager, PreviousDayUtc(18, 20), PreviousDayUtc(18, 30));

        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db, now);
        var result = await service.GetAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1),
            360,
            FollowUpSlaGrouping.Day,
            cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.Equal(1, result.Value!.Summary.Due);
        Assert.Equal(1, result.Value.Summary.Resolved);
        Assert.Equal(1, result.Value.Summary.SlaMet);
        Assert.Equal(0, result.Value.Summary.SlaBreached);
        Assert.Equal(100m, result.Value.Summary.SlaMetPercent);
    }

    private static FollowUpSlaAnalyticsService CreateService(AppDbContext db, DateTime now)
        => new(
            db,
            new FixedTimeProvider(now),
            Options.Create(new FollowUpReminderOptions
            {
                DueSoonMinutes = 30,
                EscalationAfterMinutes = 60,
                EscalationFallbackRoles = ["SuperAdmin", "Admin"],
                ScanIntervalSeconds = 60
            }));

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"follow-up-sla-{Guid.NewGuid():N}")
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

    private static User AddUserWithEmployee(
        AppDbContext db,
        string email,
        string fullName,
        string employeeCode,
        Department department)
    {
        db.Departments.Add(department);
        var user = AddUser(db, email);
        var employee = new Employee
        {
            User = user,
            UserId = user.Id,
            Department = department,
            DepartmentId = department.Id,
            EmployeeCode = employeeCode,
            NormalizedEmployeeCode = employeeCode.ToUpperInvariant(),
            FullName = fullName,
            NormalizedFullName = fullName.ToUpperInvariant(),
            JobTitle = "Manager",
            IsActive = true
        };
        user.Employee = employee;
        db.Employees.Add(employee);
        return user;
    }

    private static ProjectTask AddTask(AppDbContext db, string title)
    {
        var code = $"SL-{Guid.NewGuid():N}"[..12];
        var project = new Project
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = $"SLA {title}",
            NormalizedName = $"SLA {title}".ToUpperInvariant(),
            Status = ProjectStatus.Active
        };
        var task = new ProjectTask
        {
            Project = project,
            ProjectId = project.Id,
            Title = title,
            NormalizedTitle = title.ToUpperInvariant(),
            Status = ProjectTaskStatus.InProgress,
            Priority = ProjectTaskPriority.Normal,
            CreatedAtUtc = PreviousDayUtc(12),
            UpdatedAtUtc = Utc(12)
        };
        db.Projects.Add(project);
        db.ProjectTasks.Add(task);
        return task;
    }

    private static TaskActivity AddFollowUp(
        AppDbContext db,
        ProjectTask task,
        User manager,
        DateTime assignedAtUtc,
        DateTime dueAtUtc)
        => AddActivity(db, task, WebsiteWorkAttentionActionService.FollowUpAssignedAction, assignedAtUtc, new
        {
            actorUserId = Guid.NewGuid(),
            actorEmail = "boss@example.com",
            note = "Follow up on this target.",
            followUpOwnerUserId = manager.Id,
            followUpOwnerEmail = manager.Email,
            followUpOwnerName = manager.Employee?.FullName,
            followUpDueAtUtc = dueAtUtc
        });

    private static TaskActivity AddResolved(
        AppDbContext db,
        ProjectTask task,
        User manager,
        DateTime resolvedAtUtc,
        DateTime dueAtUtc)
        => AddActivity(db, task, WebsiteWorkAttentionActionService.FollowUpResolvedAction, resolvedAtUtc, new
        {
            actorUserId = manager.Id,
            actorEmail = manager.Email,
            followUpOwnerUserId = manager.Id,
            followUpOwnerEmail = manager.Email,
            followUpOwnerName = manager.Employee?.FullName,
            followUpDueAtUtc = dueAtUtc,
            resolvedAtUtc
        });

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

    private static DateTime Utc(int hour, int minute = 0)
        => new(2026, 10, 1, hour, minute, 0, DateTimeKind.Utc);

    private static DateTime PreviousDayUtc(int hour, int minute = 0)
        => new(2026, 9, 30, hour, minute, 0, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
