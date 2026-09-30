using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AuditLogServiceTests
{
    [Fact]
    public async Task Get_audit_logs_filters_orders_and_resolves_actor_email()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var actor = new User
        {
            Email = "admin@example.com",
            NormalizedEmail = "ADMIN@EXAMPLE.COM",
            PasswordHash = "hash"
        };
        db.Users.Add(actor);
        db.AuditLogs.AddRange(
            new AuditLog
            {
                ActorUserId = actor.Id,
                Action = "employee.created",
                TargetType = "Employee",
                TargetId = "EMP-1",
                IpAddress = "127.0.0.1",
                CreatedAtUtc = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc)
            },
            new AuditLog
            {
                Action = "auth.login.failed",
                TargetType = "User",
                TargetId = "unknown@example.com",
                CreatedAtUtc = new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc)
            },
            new AuditLog
            {
                ActorUserId = actor.Id,
                Action = "employee.updated",
                TargetType = "Employee",
                TargetId = "EMP-1",
                CreatedAtUtc = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc)
            });
        await db.SaveChangesAsync(cancellationToken);

        var service = new AuditLogService(db);
        var result = await service.GetAuditLogsAsync(
            search: "employee",
            action: null,
            targetType: "employee",
            actorUserId: actor.Id,
            fromUtc: null,
            toUtc: null,
            page: 1,
            pageSize: 10,
            cancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Collection(
            result.Items,
            newest =>
            {
                Assert.Equal("employee.updated", newest.Action);
                Assert.Equal(actor.Id, newest.ActorUserId);
                Assert.Equal("admin@example.com", newest.ActorEmail);
            },
            older => Assert.Equal("employee.created", older.Action));
    }

    [Fact]
    public async Task Get_audit_logs_clamps_page_size_to_one_hundred()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        for (var index = 0; index < 105; index++)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = "test.event",
                TargetType = "Test",
                TargetId = index.ToString(),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-index)
            });
        }
        await db.SaveChangesAsync(cancellationToken);

        var result = await new AuditLogService(db).GetAuditLogsAsync(
            null, null, null, null, null, null, 0, 500, cancellationToken);

        Assert.Equal(1, result.Page);
        Assert.Equal(100, result.PageSize);
        Assert.Equal(105, result.TotalCount);
        Assert.Equal(100, result.Items.Count);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
