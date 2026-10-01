using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class SecurityObservabilityServiceTests
{
    [Fact]
    public async Task Dashboard_correlates_security_events_and_privileged_actions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var actor = new User
        {
            Email = "security-admin@example.com",
            NormalizedEmail = "SECURITY-ADMIN@EXAMPLE.COM",
            PasswordHash = "hash"
        };
        db.Users.Add(actor);

        for (var index = 0; index < 5; index++)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = "auth.login.failed",
                TargetType = "User",
                IpAddress = "203.0.113.10",
                CreatedAtUtc = now.AddMinutes(-index - 1)
            });
        }
        for (var index = 0; index < 3; index++)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = SecurityObservabilityService.RateLimitRejectedAction,
                TargetType = "Endpoint",
                TargetId = "POST /api/auth/login",
                IpAddress = "203.0.113.10",
                CreatedAtUtc = now.AddMinutes(-index - 1)
            });
        }
        db.AuditLogs.AddRange(
            new AuditLog
            {
                ActorUserId = actor.Id,
                Action = "auth.refresh.reuse_detected",
                TargetType = "User",
                TargetId = actor.Id.ToString(),
                IpAddress = "198.51.100.20",
                CreatedAtUtc = now.AddMinutes(-2)
            },
            new AuditLog
            {
                ActorUserId = actor.Id,
                Action = "employee.updated",
                TargetType = "Employee",
                TargetId = Guid.NewGuid().ToString(),
                IpAddress = "198.51.100.20",
                CreatedAtUtc = now.AddMinutes(-3)
            });
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db);
        var dashboard = await service.GetDashboardAsync(24, cancellationToken);

        Assert.Equal(5, dashboard.Overview.FailedLogins);
        Assert.Equal(3, dashboard.Overview.RateLimitRejections);
        Assert.Equal(1, dashboard.Overview.RefreshReuseDetections);
        Assert.Equal(1, dashboard.Overview.PrivilegedActions);
        Assert.Contains(dashboard.Correlations, x => x.Kind == "FailedLoginBurst" && x.Source == "203.0.113.10");
        Assert.Contains(dashboard.Correlations, x => x.Kind == "RateLimitBurst" && x.Source == "203.0.113.10");
        Assert.Contains(dashboard.Correlations, x => x.Kind == "RefreshTokenReuse" && x.Severity == "Critical");
        Assert.Contains(dashboard.RecentPrivilegedActions, x => x.Action == "employee.updated" && x.ActorEmail == actor.Email);
        Assert.Equal("Pass", dashboard.Integrity.Status);
        Assert.Equal(0, dashboard.Integrity.StructurallyInvalidRecords);
        Assert.Equal(64, dashboard.Integrity.Sha256.Length);
    }

    [Fact]
    public async Task Export_returns_hash_verifiable_csv_and_range_validation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            Action = "roles.updated",
            TargetType = "Role",
            TargetId = "admin",
            MetadataJson = "{\"note\":\"permission review\"}",
            CreatedAtUtc = now.AddHours(-1)
        });
        await db.SaveChangesAsync(cancellationToken);

        var service = CreateService(db);
        var export = await service.ExportAsync(now.AddDays(-1), now.AddMinutes(1), cancellationToken);
        var actualHash = Convert.ToHexString(SHA256.HashData(export.Content));
        var text = System.Text.Encoding.UTF8.GetString(export.Content);

        Assert.Equal(actualHash, export.Sha256);
        Assert.Equal(1, export.RecordCount);
        Assert.False(export.Truncated);
        Assert.Contains("roles.updated", text);
        Assert.Contains("permission review", text);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(now, now.AddDays(-367), cancellationToken));
    }

    private static SecurityObservabilityService CreateService(AppDbContext db)
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
                ExportMaxRecords = 50_000
            }),
            TimeProvider.System);

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
