using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace Backend.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task Initial_migration_applies_to_real_postgres_and_supports_user_write()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "TEST_POSTGRES_CONNECTION is required for migration tests.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);

        var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        Assert.Empty(pendingMigrations);

        db.Users.Add(new User
        {
            Email = "migration-test@example.com",
            NormalizedEmail = "MIGRATION-TEST@EXAMPLE.COM",
            PasswordHash = "ci-test-hash"
        });
        await db.SaveChangesAsync(cancellationToken);

        Assert.Equal(1, await db.Users.CountAsync(cancellationToken));
    }
}
