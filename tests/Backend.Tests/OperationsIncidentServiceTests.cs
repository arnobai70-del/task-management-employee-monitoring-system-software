using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class OperationsIncidentServiceTests
{
    [Fact]
    public async Task Scan_creates_deduplicates_and_auto_resolves_operational_incidents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var employeeId = Guid.NewGuid();
        var health = new FakeOperationsHealthService(UnhealthyOverview(employeeId, now));
        var realtime = new CapturingRealtimePublisher();
        var service = CreateService(db, health, realtime, now);

        await service.ScanAsync(cancellationToken);
        var firstCount = await db.AuditLogs.CountAsync(x => x.TargetType == OperationsIncidentService.TargetType, cancellationToken);
        Assert.Equal(6, firstCount);
        var summary = await service.GetSummaryAsync(cancellationToken);
        Assert.Equal(6, summary.Open);
        Assert.Equal(3, summary.OpenCritical);
        Assert.Equal(3, summary.OpenWarning);

        await service.ScanAsync(cancellationToken);
        Assert.Equal(firstCount, await db.AuditLogs.CountAsync(x => x.TargetType == OperationsIncidentService.TargetType, cancellationToken));

        health.Overview = HealthyOverview(employeeId, now.AddMinutes(2));
        await service.ScanAsync(cancellationToken);
        summary = await service.GetSummaryAsync(cancellationToken);
        Assert.Equal(0, summary.Open);
        Assert.Equal(6, summary.ResolvedToday);
        Assert.Equal(12, await db.AuditLogs.CountAsync(x => x.TargetType == OperationsIncidentService.TargetType, cancellationToken));
        Assert.Equal(12, realtime.Events.Count);
    }

    [Fact]
    public async Task Recovered_incident_reopens_immediately_when_same_signal_returns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(now);
        var employeeId = Guid.NewGuid();
        var health = new FakeOperationsHealthService(HealthyOverview(employeeId, now) with
        {
            Backup = new OperationsBackupHealthResponse(false, null, null, true, null, null, null)
        });
        var realtime = new CapturingRealtimePublisher();
        var service = CreateService(db, health, realtime, clock);

        await service.ScanAsync(cancellationToken);
        var incident = (await service.GetAsync(null, null, OperationsIncidentKind.BackupStale, null, 1, 10, cancellationToken)).Items.Single();
        Assert.Equal(OperationsIncidentStatus.Open, incident.Status);
        Assert.Equal(1, incident.OccurrenceCount);

        clock.UtcNow = now.AddMinutes(1);
        health.Overview = HealthyOverview(employeeId, clock.UtcNow);
        await service.ScanAsync(cancellationToken);
        Assert.Equal(OperationsIncidentStatus.Resolved, (await service.GetByIdAsync(incident.Id, cancellationToken)).Value!.Status);

        clock.UtcNow = now.AddMinutes(2);
        health.Overview = HealthyOverview(employeeId, clock.UtcNow) with
        {
            Backup = new OperationsBackupHealthResponse(false, null, null, true, null, null, null)
        };
        await service.ScanAsync(cancellationToken);
        var reopened = (await service.GetByIdAsync(incident.Id, cancellationToken)).Value!;
        Assert.Equal(OperationsIncidentStatus.Open, reopened.Status);
        Assert.Equal(2, reopened.OccurrenceCount);
        Assert.Contains(reopened.History, item => item.Action == "Reopened");
    }

    [Fact]
    public async Task Manager_can_acknowledge_assign_and_resolve_with_durable_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var manager = AddOperationsManager(db, "ops@example.com", "Ops Manager");
        await db.SaveChangesAsync(cancellationToken);

        var health = new FakeOperationsHealthService(HealthyOverview(Guid.NewGuid(), now) with
        {
            Backup = new OperationsBackupHealthResponse(false, null, null, true, null, null, null)
        });
        var realtime = new CapturingRealtimePublisher();
        var service = CreateService(db, health, realtime, now);
        await service.ScanAsync(cancellationToken);
        var incident = (await service.GetAsync(null, null, null, null, 1, 10, cancellationToken)).Items.Single();
        var actor = new RequestActor(manager.Id, "127.0.0.1", "tests");

        var acknowledged = await service.AcknowledgeAsync(
            incident.Id,
            new OperationsIncidentAcknowledgeRequest("Investigating backup schedule."),
            actor,
            cancellationToken);
        Assert.Equal(OperationStatus.Success, acknowledged.Status);
        Assert.Equal(OperationsIncidentStatus.Acknowledged, acknowledged.Value!.Status);

        var assigned = await service.AssignAsync(
            incident.Id,
            new OperationsIncidentAssignRequest(manager.Id, "Own this incident."),
            actor,
            cancellationToken);
        Assert.Equal(OperationStatus.Success, assigned.Status);
        Assert.Equal(manager.Id, assigned.Value!.OwnerUserId);

        var resolved = await service.ResolveAsync(
            incident.Id,
            new OperationsIncidentResolveRequest("Backup job repaired."),
            actor,
            cancellationToken);
        Assert.Equal(OperationStatus.Success, resolved.Status);
        Assert.Equal(OperationsIncidentStatus.Resolved, resolved.Value!.Status);
        Assert.Equal("Manual", resolved.Value.ResolutionKind);
        Assert.Contains(resolved.Value.History, item => item.Action == "Acknowledged" && item.ActorEmail == manager.Email);
        Assert.Contains(resolved.Value.History, item => item.Action == "Assigned");
        Assert.Contains(resolved.Value.History, item => item.Action == "Resolved" && item.Note == "Backup job repaired.");

        var assignees = await service.GetAssigneesAsync(cancellationToken);
        Assert.Contains(assignees, item => item.UserId == manager.Id);
    }

    private static OperationsIncidentService CreateService(
        AppDbContext db,
        IOperationsHealthService health,
        IOperationsIncidentRealtimePublisher realtime,
        DateTime now)
        => CreateService(db, health, realtime, new MutableTimeProvider(now));

    private static OperationsIncidentService CreateService(
        AppDbContext db,
        IOperationsHealthService health,
        IOperationsIncidentRealtimePublisher realtime,
        TimeProvider timeProvider)
        => new(
            db,
            health,
            Options.Create(new OperationsOptions
            {
                DetailedAgentStaleMinutes = 3,
                BackupStaleHours = 26,
                AgentOfflineMinutes = 5,
                DatabaseLatencyWarningMilliseconds = 1000,
                IncidentScanIntervalSeconds = 60,
                IncidentReopenCooldownMinutes = 30
            }),
            timeProvider,
            realtime);

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"operations-incidents-{Guid.NewGuid():N}")
            .Options);

    private static User AddOperationsManager(AppDbContext db, string email, string name)
    {
        var managePermission = new Permission
        {
            Code = PermissionCatalog.OperationsManage,
            Description = "Manage operations incidents."
        };
        var reportsPermission = new Permission
        {
            Code = PermissionCatalog.ReportsRead,
            Description = "View operational reports."
        };
        var role = new Role { Name = $"Ops-{Guid.NewGuid():N}", IsActive = true };
        role.RolePermissions.Add(new RolePermission
        {
            Role = role,
            RoleId = role.Id,
            Permission = managePermission,
            PermissionId = managePermission.Id
        });
        role.RolePermissions.Add(new RolePermission
        {
            Role = role,
            RoleId = role.Id,
            Permission = reportsPermission,
            PermissionId = reportsPermission.Id
        });
        var user = new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "hash",
            IsActive = true
        };
        user.UserRoles.Add(new UserRole { User = user, UserId = user.Id, Role = role, RoleId = role.Id });
        var employee = new Employee
        {
            User = user,
            UserId = user.Id,
            EmployeeCode = "OPS-001",
            NormalizedEmployeeCode = "OPS-001",
            FullName = name,
            NormalizedFullName = name.ToUpperInvariant(),
            JobTitle = "Operations Manager",
            IsActive = true
        };
        user.Employee = employee;
        db.Permissions.AddRange(managePermission, reportsPermission);
        db.Roles.Add(role);
        db.Users.Add(user);
        db.Employees.Add(employee);
        return user;
    }

    private static OperationsOverviewResponse UnhealthyOverview(Guid employeeId, DateTime now)
        => new(
            now,
            new OperationsServerHealthResponse(true, true, 1500, now.AddHours(-1), "1.0.0"),
            new OperationsBackupHealthResponse(true, now.AddHours(-30), 1800, true, "nightly", "backup.dump", 1024),
            new OperationsReleaseHealthResponse(true, "stable", "2.0.0", now.AddHours(-2)),
            new OperationsAgentSummaryResponse(1, 0, 1, 0, 1, 1, 1, 1, 1),
            [new OperationsAgentHealthResponse(
                employeeId,
                "EMP-001",
                "Worker One",
                null,
                "Operations",
                false,
                now.AddMinutes(-10),
                now.AddMinutes(-1),
                "PC-01",
                "1.0.0",
                "1.0.0",
                "1.0.0",
                false,
                "1.0.0",
                "stable",
                now.AddDays(-1),
                now.AddMinutes(-2),
                true,
                "Critical",
                ["offline", "service stopped", "outdated", "rollback"])]);

    private static OperationsOverviewResponse HealthyOverview(Guid employeeId, DateTime now)
        => new(
            now,
            new OperationsServerHealthResponse(true, true, 20, now.AddHours(-1), "1.0.0"),
            new OperationsBackupHealthResponse(true, now.AddMinutes(-10), 10, false, "nightly", "backup.dump", 1024),
            new OperationsReleaseHealthResponse(true, "stable", "2.0.0", now.AddHours(-2)),
            new OperationsAgentSummaryResponse(1, 1, 0, 1, 0, 1, 0, 0, 0),
            [new OperationsAgentHealthResponse(
                employeeId,
                "EMP-001",
                "Worker One",
                null,
                "Operations",
                true,
                now,
                now,
                "PC-01",
                "2.0.0",
                "2.0.0",
                "1.0.0",
                true,
                "2.0.0",
                "stable",
                now.AddMinutes(-20),
                null,
                false,
                "Healthy",
                [])]);

    private sealed class FakeOperationsHealthService(OperationsOverviewResponse overview) : IOperationsHealthService
    {
        public OperationsOverviewResponse Overview { get; set; } = overview;

        public Task<OperationResult<AgentHealthReportResponse>> RecordAgentHealthAsync(
            RequestActor actor,
            AgentHealthReportRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(OperationResult<AgentHealthReportResponse>.Invalid("not_used", "Not used by this test."));

        public Task<OperationsOverviewResponse> GetOverviewAsync(
            string? search,
            Guid? departmentId,
            string? health,
            int limit,
            CancellationToken cancellationToken)
            => Task.FromResult(Overview);
    }

    private sealed class CapturingRealtimePublisher : IOperationsIncidentRealtimePublisher
    {
        public List<OperationsIncidentChangedResponse> Events { get; } = [];

        public Task PublishAsync(OperationsIncidentChangedResponse incident, CancellationToken cancellationToken)
        {
            Events.Add(incident);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }
}
