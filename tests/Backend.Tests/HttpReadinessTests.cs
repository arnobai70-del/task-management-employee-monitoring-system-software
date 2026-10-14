using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TaskMonitoring.Api.Data;

namespace Backend.Tests;

/// <summary>
/// /health/ready must stay unhealthy while the schema is behind the API image, because production runs the
/// migrator separately with AutoMigrate off and deploy.sh trusts this endpoint.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class HttpReadinessTests
{
    [Fact]
    public async Task Ready_reports_unhealthy_while_a_migration_is_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(baseConnection), "TEST_POSTGRES_CONNECTION is required for readiness tests.");
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"tm_readiness_{Guid.NewGuid():N}"
        }.ConnectionString;

        var previous = new Dictionary<string, string?>();
        void Set(string key, string value)
        {
            previous.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }

        Set("ConnectionStrings__DefaultConnection", connectionString);
        Set("Jwt__SigningKey", "http-readiness-test-signing-key-0123456789abcdef");
        Set("Database__AutoMigrate", "true");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        try
        {
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", ct)).StatusCode);

            string latest;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                latest = (await db.Database.GetAppliedMigrationsAsync(ct)).Last();
                await db.Database.ExecuteSqlAsync($"""DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = {latest}""", ct);
            }

            var pending = await client.GetAsync("/health/ready", ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, pending.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", ct)).StatusCode);
        }
        finally
        {
            await factory.DisposeAsync();
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            NpgsqlConnection.ClearAllPools();
            await using var cleanup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
            await cleanup.Database.EnsureDeletedAsync(ct);
        }
    }
}
