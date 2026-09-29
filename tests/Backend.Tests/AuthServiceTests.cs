using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class AuthServiceTests
{
    [Fact]
    public async Task Login_with_valid_credentials_issues_tokens_and_stores_only_refresh_hash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var user = CreateUser("developer@example.com", "Correct-Horse-Battery-42");
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var result = await service.LoginAsync(new LoginRequest(user.Email, "Correct-Horse-Battery-42"), "127.0.0.1", "tests", cancellationToken);

        Assert.Equal(AuthStatus.Success, result.Status);
        Assert.NotNull(result.Response);
        Assert.NotEmpty(result.Response.AccessToken);
        Assert.NotEmpty(result.Response.RefreshToken);
        var stored = await db.RefreshTokens.SingleAsync(cancellationToken);
        Assert.NotEqual(result.Response.RefreshToken, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.Contains(await db.AuditLogs.ToListAsync(cancellationToken), x => x.Action == "auth.login.succeeded");
    }

    [Fact]
    public async Task Reusing_rotated_refresh_token_revokes_active_family()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var user = CreateUser("manager@example.com", "Correct-Horse-Battery-43");
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        var login = await service.LoginAsync(new LoginRequest(user.Email, "Correct-Horse-Battery-43"), "127.0.0.1", "tests", cancellationToken);
        var firstRefresh = login.Response!.RefreshToken;
        var rotation = await service.RefreshAsync(new RefreshRequest(firstRefresh), "127.0.0.1", "tests", cancellationToken);
        Assert.Equal(AuthStatus.Success, rotation.Status);

        var reuse = await service.RefreshAsync(new RefreshRequest(firstRefresh), "127.0.0.1", "tests", cancellationToken);

        Assert.Equal(AuthStatus.InvalidRefreshToken, reuse.Status);
        var tokens = await db.RefreshTokens.ToListAsync(cancellationToken);
        Assert.All(tokens, token => Assert.True(token.IsRevoked));
        Assert.Contains(await db.AuditLogs.ToListAsync(cancellationToken), x => x.Action == "auth.refresh.reuse_detected");
    }

    [Fact]
    public async Task Five_failed_logins_lock_the_account_temporarily()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var user = CreateUser("employee@example.com", "Correct-Horse-Battery-44");
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        var service = CreateService(db);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await service.LoginAsync(new LoginRequest(user.Email, "wrong-password"), null, "tests", cancellationToken);
            Assert.Equal(AuthStatus.InvalidCredentials, result.Status);
        }

        var stored = await db.Users.SingleAsync(cancellationToken);
        Assert.NotNull(stored.LockoutEndUtc);
        Assert.True(stored.LockoutEndUtc > DateTime.UtcNow);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
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
        var passwordHasher = new PasswordHasher<User>();
        return new AuthService(db, passwordHasher, new TokenService(jwt), TimeProvider.System);
    }

    private static User CreateUser(string email, string password)
    {
        var user = new User
        {
            Email = email,
            NormalizedEmail = AuthService.NormalizeEmail(email),
            IsActive = true
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        return user;
    }
}
