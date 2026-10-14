using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

/// <summary>
/// G-24 through the real HTTP pipeline and PostgreSQL: rejected sign-in requests (HTTP 429) must not
/// each become an audit insert, and an over-long User-Agent must not stop the rejection being audited.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class HttpRateLimitAuditTests
{
    [Fact]
    public async Task Repeated_rejections_from_one_source_write_one_audit_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await ApiInstance.StartAsync("tm_rate_audit");
        using var client = api.Factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var index = 0; index < 25; index++)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.invalid", password = "wrong-password-1" }, ct);
            statuses.Add(response.StatusCode);
        }

        var rejected = statuses.Count(x => x == HttpStatusCode.TooManyRequests);
        Assert.True(rejected >= 10, $"expected the auth limiter to reject at least 10 of 25 requests, got {rejected}");
        Assert.Equal(1, await api.CountRateLimitRowsAsync(ct));
    }

    [Fact]
    public async Task Rejection_with_an_over_long_user_agent_is_still_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var api = await ApiInstance.StartAsync("tm_rate_audit_ua");
        using var client = api.Factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "agent/" + new string('x', 1000));

        for (var index = 0; index < 12; index++)
        {
            using var _ = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.invalid", password = "wrong-password-1" }, ct);
        }

        Assert.Equal(1, await api.CountRateLimitRowsAsync(ct));
    }

    private sealed class ApiInstance : IAsyncDisposable
    {
        private readonly Dictionary<string, string?> _previousEnvironment = new();
        private string _connectionString = string.Empty;
        public WebApplicationFactory<Program> Factory { get; private set; } = null!;

        public static async Task<ApiInstance> StartAsync(string prefix)
        {
            var baseConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(baseConnection), "TEST_POSTGRES_CONNECTION is required for rate-limit audit tests.");
            var instance = new ApiInstance
            {
                _connectionString = new NpgsqlConnectionStringBuilder(baseConnection) { Database = $"{prefix}_{Guid.NewGuid():N}" }.ConnectionString
            };
            instance.SetEnvironment("ConnectionStrings__DefaultConnection", instance._connectionString);
            instance.SetEnvironment("Jwt__SigningKey", "http-rate-limit-audit-test-signing-key-0123456789");
            instance.SetEnvironment("Database__AutoMigrate", "true");
            instance.Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
            _ = instance.Factory.Server;
            await Task.CompletedTask;
            return instance;
        }

        public async Task<int> CountRateLimitRowsAsync(CancellationToken ct)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.AuditLogs.CountAsync(x => x.Action == SecurityObservabilityService.RateLimitRejectedAction, ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            foreach (var (key, value) in _previousEnvironment)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            NpgsqlConnection.ClearAllPools();
            await using var cleanup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
            await cleanup.Database.EnsureDeletedAsync();
        }

        private void SetEnvironment(string key, string value)
        {
            _previousEnvironment.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
