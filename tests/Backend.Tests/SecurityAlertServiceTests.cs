using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class SecurityAlertServiceTests
{
    [Fact]
    public async Task Scan_creates_deduplicates_and_auto_resolves_correlated_alerts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 6, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(now);
        var realtime = new CapturingRealtimePublisher();
        SeedSecuritySignals(db, now, "203.0.113.10");
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db, realtime, clock);

        await service.ScanAsync(cancellationToken);
        var firstCount = await db.AuditLogs.CountAsync(x => x.TargetType == SecurityAlertService.TargetType, cancellationToken);
        Assert.Equal(3, firstCount);
        var summary = await service.GetSummaryAsync(cancellationToken);
        Assert.Equal(3, summary.Open);
        Assert.Equal(2, summary.CriticalActive);

        await service.ScanAsync(cancellationToken);
        Assert.Equal(firstCount, await db.AuditLogs.CountAsync(x => x.TargetType == SecurityAlertService.TargetType, cancellationToken));

        clock.UtcNow = now.AddMinutes(20);
        await service.ScanAsync(cancellationToken);
        summary = await service.GetSummaryAsync(cancellationToken);
        Assert.Equal(0, summary.Open);
        Assert.Equal(0, summary.Escalated);
        Assert.Equal(3, summary.ResolvedToday);
        Assert.Equal(6, await db.AuditLogs.CountAsync(x => x.TargetType == SecurityAlertService.TargetType, cancellationToken));
        Assert.Equal(6, realtime.Events.Count);
    }

    [Fact]
    public async Task Unacknowledged_critical_alert_escalates_once_after_sla()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(now);
        var realtime = new CapturingRealtimePublisher();
        for (var index = 0; index < 10; index++)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = "auth.login.failed",
                TargetType = "User",
                IpAddress = "198.51.100.7",
                CreatedAtUtc = now.AddMinutes(-6).AddSeconds(index)
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db, realtime, clock);

        await service.ScanAsync(cancellationToken);
        var alert = (await service.GetAsync(null, null, SecurityAlertKind.FailedLoginBurst, null, 1, 10, cancellationToken)).Items.Single();
        Assert.Equal(SecurityAlertStatus.Open, alert.Status);
        Assert.Equal(SecurityAlertSeverity.Critical, alert.Severity);

        await service.ScanAsync(cancellationToken);
        alert = (await service.GetByIdAsync(alert.Id, cancellationToken)).Value!;
        Assert.Equal(SecurityAlertStatus.Escalated, alert.Status);
        Assert.NotNull(alert.EscalatedAtUtc);
        Assert.Contains(alert.History, item => item.Action == "Escalated");

        var eventCount = await db.AuditLogs.CountAsync(x => x.TargetType == SecurityAlertService.TargetType, cancellationToken);
        await service.ScanAsync(cancellationToken);
        Assert.Equal(eventCount, await db.AuditLogs.CountAsync(x => x.TargetType == SecurityAlertService.TargetType, cancellationToken));
    }

    [Fact]
    public async Task Manager_can_acknowledge_assign_and_resolve_with_durable_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var manager = AddSecurityManager(db, "security@example.com", "Security Manager");
        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = manager.Id,
            Action = "auth.refresh.reuse_detected",
            TargetType = "User",
            TargetId = manager.Id.ToString(),
            IpAddress = "192.0.2.40",
            CreatedAtUtc = now.AddMinutes(-1)
        });
        await db.SaveChangesAsync(cancellationToken);
        var realtime = new CapturingRealtimePublisher();
        var service = CreateService(db, realtime, new MutableTimeProvider(now));
        await service.ScanAsync(cancellationToken);
        var alert = (await service.GetAsync(null, null, SecurityAlertKind.RefreshTokenReuse, null, 1, 10, cancellationToken)).Items.Single();
        var actor = new RequestActor(manager.Id, "127.0.0.1", "tests");

        var acknowledged = await service.AcknowledgeAsync(
            alert.Id,
            new SecurityAlertAcknowledgeRequest("Investigating session reuse."),
            actor,
            cancellationToken);
        Assert.Equal(OperationStatus.Success, acknowledged.Status);
        Assert.Equal(SecurityAlertStatus.Acknowledged, acknowledged.Value!.Status);

        var assigned = await service.AssignAsync(
            alert.Id,
            new SecurityAlertAssignRequest(manager.Id, "Taking ownership."),
            actor,
            cancellationToken);
        Assert.Equal(OperationStatus.Success, assigned.Status);
        Assert.Equal(manager.Id, assigned.Value!.OwnerUserId);

        var resolved = await service.ResolveAsync(
            alert.Id,
            new SecurityAlertResolveRequest("Sessions rotated and account reviewed."),
            actor,
            cancellationToken);
        Assert.Equal(OperationStatus.Success, resolved.Status);
        Assert.Equal(SecurityAlertStatus.Resolved, resolved.Value!.Status);
        Assert.Equal("Manual", resolved.Value.ResolutionKind);
        Assert.Contains(resolved.Value.History, item => item.Action == "Acknowledged" && item.ActorEmail == manager.Email);
        Assert.Contains(resolved.Value.History, item => item.Action == "Assigned");
        Assert.Contains(resolved.Value.History, item => item.Action == "Resolved" && item.Note == "Sessions rotated and account reviewed.");

        var assignees = await service.GetAssigneesAsync(cancellationToken);
        Assert.Contains(assignees, item => item.UserId == manager.Id);
    }

    [Fact]
    public async Task Acknowledged_critical_alert_does_not_auto_escalate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDb();
        var manager = AddSecurityManager(db, "ack@example.com", "Ack Manager");
        for (var index = 0; index < 10; index++)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = "auth.login.failed",
                TargetType = "User",
                IpAddress = "203.0.113.99",
                CreatedAtUtc = now.AddMinutes(-6).AddSeconds(index)
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        var clock = new MutableTimeProvider(now);
        var service = CreateService(db, new CapturingRealtimePublisher(), clock);
        await service.ScanAsync(cancellationToken);
        var alert = (await service.GetAsync(null, null, null, null, 1, 10, cancellationToken)).Items.Single();
        await service.AcknowledgeAsync(alert.Id, new SecurityAlertAcknowledgeRequest(null), new RequestActor(manager.Id, null, null), cancellationToken);

        await service.ScanAsync(cancellationToken);
        var current = (await service.GetByIdAsync(alert.Id, cancellationToken)).Value!;
        Assert.Equal(SecurityAlertStatus.Acknowledged, current.Status);
        Assert.Null(current.EscalatedAtUtc);
    }

    private static void SeedSecuritySignals(AppDbContext db, DateTime now, string ip)
    {
        for (var index = 0; index < 5; index++)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = "auth.login.failed",
                TargetType = "User",
                IpAddress = ip,
                CreatedAtUtc = now.AddSeconds(-index - 1)
            });
        }
        for (var index = 0; index < 6; index++)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = SecurityObservabilityService.RateLimitRejectedAction,
                TargetType = "Endpoint",
                TargetId = "POST /api/auth/login",
                IpAddress = ip,
                CreatedAtUtc = now.AddSeconds(-index - 1)
            });
        }
        db.AuditLogs.Add(new AuditLog
        {
            Action = "auth.refresh.reuse_detected",
            TargetType = "User",
            TargetId = Guid.NewGuid().ToString(),
            IpAddress = "198.51.100.20",
            CreatedAtUtc = now.AddSeconds(-2)
        });
    }

    private static SecurityAlertService CreateService(
        AppDbContext db,
        ISecurityAlertRealtimePublisher realtime,
        TimeProvider timeProvider)
        => new(
            db,
            Options.Create(new SecurityObservabilityOptions
            {
                DefaultWindowHours = 24,
                MaxWindowHours = 168,
                CorrelationWindowMinutes = 15,
                FailedLoginThreshold = 5,
                RateLimitThreshold = 3,
                MinimumAuditRetentionDays = 90,
                ExportMaxRecords = 50_000,
                AlertScanIntervalSeconds = 60,
                AlertEscalationAfterMinutes = 5,
                AlertReopenCooldownMinutes = 15
            }),
            timeProvider,
            realtime);

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"security-alerts-{Guid.NewGuid():N}")
            .Options);

    private static User AddSecurityManager(AppDbContext db, string email, string name)
    {
        var managePermission = new Permission
        {
            Code = PermissionCatalog.SecurityAlertsManage,
            Description = "Manage security alerts."
        };
        var auditPermission = new Permission
        {
            Code = PermissionCatalog.AuditRead,
            Description = "Read security audit history."
        };
        var role = new Role { Name = $"Security-{Guid.NewGuid():N}", IsActive = true };
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
            Permission = auditPermission,
            PermissionId = auditPermission.Id
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
            EmployeeCode = $"SEC-{Guid.NewGuid():N}"[..12],
            NormalizedEmployeeCode = $"SEC-{Guid.NewGuid():N}"[..12],
            FullName = name,
            NormalizedFullName = name.ToUpperInvariant(),
            JobTitle = "Security Manager",
            IsActive = true
        };
        user.Employee = employee;
        db.Permissions.AddRange(managePermission, auditPermission);
        db.Roles.Add(role);
        db.Users.Add(user);
        db.Employees.Add(employee);
        return user;
    }

    private sealed class CapturingRealtimePublisher : ISecurityAlertRealtimePublisher
    {
        public List<SecurityAlertChangedResponse> Events { get; } = [];
        public Task PublishAsync(SecurityAlertChangedResponse alert, CancellationToken cancellationToken)
        {
            Events.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }
}
