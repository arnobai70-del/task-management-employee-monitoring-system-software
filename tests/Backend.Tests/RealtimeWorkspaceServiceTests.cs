using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Infrastructure;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class RealtimeWorkspaceServiceTests
{
    [Fact]
    public async Task Heartbeat_reports_on_break_from_authenticated_employee_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "P-001", "Presence Employee");
        var shift = AddShift(db);
        var assignment = new EmployeeShiftAssignment { EmployeeId = employee.Id, Employee = employee, ShiftId = shift.Id, Shift = shift, EffectiveFrom = new DateOnly(2026, 9, 30) };
        var session = new WorkSession
        {
            EmployeeId = employee.Id, Employee = employee, ShiftId = shift.Id, Shift = shift, ShiftAssignmentId = assignment.Id, ShiftAssignment = assignment,
            WorkDate = new DateOnly(2026, 9, 30), ScheduledStartUtc = now.AddHours(-1), ScheduledEndUtc = now.AddHours(7), StartedAtUtc = now.AddMinutes(-30)
        };
        session.Breaks.Add(new WorkBreak { WorkSessionId = session.Id, WorkSession = session, StartedAtUtc = now.AddMinutes(-5) });
        db.EmployeeShiftAssignments.Add(assignment);
        db.WorkSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        var publisher = new CapturePublisher();
        var service = CreateService(db, now, publisher);
        var result = await service.RecordHeartbeatAsync(new RequestActor(employee.UserId, null, null), new PresenceHeartbeatRequest { ClientKind = "desktop", ClientVersion = "1.0" }, cancellationToken);

        Assert.Equal(OperationStatus.Success, result.Status);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.IsOnline);
        Assert.Equal("OnBreak", result.Value.WorkState);
        Assert.Single(publisher.Presences);
        Assert.Equal(1, await db.EmployeePresences.CountAsync(cancellationToken));
    }

    [Fact]
    public async Task Roster_marks_stale_presence_offline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employee = AddEmployee(db, "P-002", "Offline Employee");
        db.EmployeePresences.Add(new EmployeePresence { EmployeeId = employee.Id, Employee = employee, LastSeenAtUtc = now.AddMinutes(-5), ClientKind = "desktop" });
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db, now, new CapturePublisher());
        var roster = await service.GetPresenceRosterAsync(null, null, 1, 100, cancellationToken);

        var item = Assert.Single(roster.Items);
        Assert.False(item.IsOnline);
        Assert.Equal("Offline", item.WorkState);
    }

    [Fact]
    public async Task Employee_cannot_mark_another_employees_notification_read()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var first = AddEmployee(db, "P-003", "First Employee");
        var second = AddEmployee(db, "P-004", "Second Employee");
        var notification = new EmployeeNotification { EmployeeId = second.Id, Employee = second, Kind = EmployeeNotificationKind.TaskAssigned, Title = "Assigned", Message = "Task assigned", EntityType = "ProjectTask", CreatedAtUtc = now };
        db.EmployeeNotifications.Add(notification);
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db, now, new CapturePublisher());
        var result = await service.MarkMyNotificationReadAsync(new RequestActor(first.UserId, null, null), notification.Id, cancellationToken);

        Assert.Equal(OperationStatus.NotFound, result.Status);
        Assert.Null((await db.EmployeeNotifications.SingleAsync(cancellationToken)).ReadAtUtc);
    }

    [Fact]
    public async Task Task_interceptor_creates_durable_assignment_and_reassignment_notifications()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
        var publisher = new CapturePublisher();
        var interceptor = new TaskNotificationInterceptor(new FixedTimeProvider(now), publisher, NullLogger<TaskNotificationInterceptor>.Instance);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"realtime-interceptor-{Guid.NewGuid():N}")
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new AppDbContext(options);
        var first = AddEmployee(db, "P-005", "First Assignee");
        var second = AddEmployee(db, "P-006", "Second Assignee");
        var project = new Project { Code = "RT", NormalizedCode = "RT", Name = "Realtime", NormalizedName = "REALTIME", Status = ProjectStatus.Active };
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken);

        var task = new ProjectTask { ProjectId = project.Id, Project = project, Title = "Realtime task", NormalizedTitle = "REALTIME TASK", AssigneeEmployeeId = first.Id, AssigneeEmployee = first };
        db.ProjectTasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);

        task.AssigneeEmployeeId = second.Id;
        task.AssigneeEmployee = second;
        await db.SaveChangesAsync(cancellationToken);

        var notifications = await db.EmployeeNotifications.OrderBy(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
        Assert.Equal(3, notifications.Count);
        Assert.Contains(notifications, x => x.EmployeeId == first.Id && x.Kind == EmployeeNotificationKind.TaskAssigned);
        Assert.Contains(notifications, x => x.EmployeeId == first.Id && x.Kind == EmployeeNotificationKind.TaskUnassigned);
        Assert.Contains(notifications, x => x.EmployeeId == second.Id && x.Kind == EmployeeNotificationKind.TaskAssigned);
        Assert.Equal(3, publisher.Notifications.Count);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"realtime-{Guid.NewGuid():N}").Options);

    private static Employee AddEmployee(AppDbContext db, string code, string name)
    {
        var user = new User { Email = $"{code.ToLowerInvariant()}@example.com", NormalizedEmail = $"{code.ToUpperInvariant()}@EXAMPLE.COM", PasswordHash = "hash" };
        var employee = new Employee { UserId = user.Id, User = user, EmployeeCode = code, NormalizedEmployeeCode = code.ToUpperInvariant(), FullName = name, NormalizedFullName = name.ToUpperInvariant(), JobTitle = "Tester" };
        db.Users.Add(user);
        db.Employees.Add(employee);
        return employee;
    }

    private static Shift AddShift(AppDbContext db)
    {
        var shift = new Shift { Code = "RT-DAY", NormalizedCode = "RT-DAY", Name = "Realtime Day", NormalizedName = "REALTIME DAY", StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), TimeZoneId = "UTC" };
        db.Shifts.Add(shift);
        return shift;
    }

    private static RealtimeWorkspaceService CreateService(AppDbContext db, DateTime now, CapturePublisher publisher)
        => new(db, new FixedTimeProvider(now), Options.Create(new PresenceOptions { OnlineThresholdSeconds = 90 }), publisher);

    private sealed class CapturePublisher : IRealtimeEventPublisher
    {
        public List<EmployeePresenceResponse> Presences { get; } = [];
        public List<(Guid EmployeeId, EmployeeNotificationResponse Notification)> Notifications { get; } = [];
        public Task PublishPresenceAsync(EmployeePresenceResponse presence, CancellationToken cancellationToken) { Presences.Add(presence); return Task.CompletedTask; }
        public Task PublishNotificationAsync(Guid employeeId, EmployeeNotificationResponse notification, CancellationToken cancellationToken) { Notifications.Add((employeeId, notification)); return Task.CompletedTask; }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
