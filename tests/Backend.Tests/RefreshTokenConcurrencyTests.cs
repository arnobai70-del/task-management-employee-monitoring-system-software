using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class RefreshTokenConcurrencyTests
{
    [Fact]
    public async Task Concurrent_refreshes_with_the_same_token_issue_at_most_one_replacement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var baseConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(baseConnection), "TEST_POSTGRES_CONNECTION is required for refresh concurrency tests.");

        var connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"tm_refresh_race_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;

        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.MigrateAsync(cancellationToken);
        }

        try
        {
            string refreshToken;
            await using (var db = new AppDbContext(options))
            {
                var user = new User
                {
                    Email = "race@example.com",
                    NormalizedEmail = AuthService.NormalizeEmail("race@example.com"),
                    IsActive = true
                };
                user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "Correct-Horse-Battery-77");
                db.Users.Add(user);
                await db.SaveChangesAsync(cancellationToken);

                var login = await CreateService(db).LoginAsync(
                    new LoginRequest(user.Email, "Correct-Horse-Battery-77"), "127.0.0.1", "tests", cancellationToken);
                Assert.Equal(AuthStatus.Success, login.Status);
                refreshToken = login.Response!.RefreshToken;
            }

            const int attempts = 8;
            using var gate = new SemaphoreSlim(0, attempts);
            var tasks = Enumerable.Range(0, attempts).Select(async _ =>
            {
                await using var db = new AppDbContext(options);
                await db.Database.OpenConnectionAsync(cancellationToken);
                await gate.WaitAsync(cancellationToken);
                return await CreateService(db).RefreshAsync(new RefreshRequest(refreshToken), "127.0.0.1", "tests", cancellationToken);
            }).ToArray();
            gate.Release(attempts);
            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(x => x.Status == AuthStatus.Success));

            await using var verify = new AppDbContext(options);
            var activeTokens = await verify.RefreshTokens.CountAsync(x => x.RevokedAtUtc == null, cancellationToken);
            Assert.True(activeTokens <= 1, $"Expected at most one active refresh token after concurrent rotation, found {activeTokens}.");
        }
        finally
        {
            await using var cleanup = new AppDbContext(options);
            await cleanup.Database.EnsureDeletedAsync(cancellationToken);
        }
    }

    private static AuthService CreateService(AppDbContext db)
    {
        var jwt = Options.Create(new JwtOptions
        {
            Issuer = "tests",
            Audience = "tests",
            SigningKey = "this-is-a-test-signing-key-with-at-least-32-bytes",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7
        });
        return new AuthService(db, new PasswordHasher<User>(), new TokenService(jwt), TimeProvider.System);
    }
}
