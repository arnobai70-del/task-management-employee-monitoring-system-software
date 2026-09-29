using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Infrastructure;
using TaskMonitoring.Api.Security;

namespace Backend.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task Initial_migration_applies_to_real_postgres_and_foundation_seed_is_idempotent()
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

        var initializer = new DatabaseInitializer(
            db,
            new PasswordHasher<User>(),
            new ConfigurationBuilder().Build(),
            NullLogger<DatabaseInitializer>.Instance);

        await initializer.SeedFoundationAsync(cancellationToken);
        await initializer.SeedFoundationAsync(cancellationToken);

        Assert.Equal(9, await db.Roles.CountAsync(cancellationToken));
        Assert.Equal(PermissionCatalog.Definitions.Count, await db.Permissions.CountAsync(cancellationToken));

        var superAdmin = await db.Roles
            .Include(x => x.RolePermissions)
            .SingleAsync(x => x.Name == "SuperAdmin", cancellationToken);
        Assert.Equal(PermissionCatalog.Definitions.Count, superAdmin.RolePermissions.Count);

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
