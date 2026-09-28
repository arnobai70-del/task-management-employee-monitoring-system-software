using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public enum AuthStatus
{
    Success,
    InvalidCredentials,
    AccountUnavailable,
    InvalidRefreshToken
}

public sealed record AuthResult(AuthStatus Status, AuthResponse? Response = null);

public interface IAuthService
{
    Task<AuthResult> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken cancellationToken);
    Task<AuthResult> RefreshAsync(RefreshRequest request, string? ipAddress, string? userAgent, CancellationToken cancellationToken);
    Task RevokeAsync(Guid userId, string refreshToken, string? ipAddress, CancellationToken cancellationToken);
}

public sealed class AuthService(
    AppDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    ITokenService tokenService,
    TimeProvider timeProvider) : IAuthService
{
    private const int MaxFailedLoginAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public async Task<AuthResult> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var normalizedEmail = NormalizeEmail(request.Email);
        var user = await LoadUserAsync(normalizedEmail, cancellationToken);

        if (user is null)
        {
            dbContext.AuditLogs.Add(CreateAudit(null, "auth.login.failed", "User", null, "unknown_user", ipAddress, userAgent, now));
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        if (!user.IsActive || (user.LockoutEndUtc.HasValue && user.LockoutEndUtc > now))
        {
            dbContext.AuditLogs.Add(CreateAudit(user.Id, "auth.login.blocked", "User", user.Id.ToString(), "inactive_or_locked", ipAddress, userAgent, now));
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AuthResult(AuthStatus.AccountUnavailable);
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
            {
                user.LockoutEndUtc = now.Add(LockoutDuration);
                user.FailedLoginAttempts = 0;
            }

            user.UpdatedAtUtc = now;
            dbContext.AuditLogs.Add(CreateAudit(user.Id, "auth.login.failed", "User", user.Id.ToString(), "invalid_password", ipAddress, userAgent, now));
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        }

        user.FailedLoginAttempts = 0;
        user.LockoutEndUtc = null;
        user.UpdatedAtUtc = now;

        var issued = IssueForUser(user, now);
        dbContext.RefreshTokens.Add(CreateRefreshToken(user.Id, issued, ipAddress, now));
        dbContext.AuditLogs.Add(CreateAudit(user.Id, "auth.login.succeeded", "User", user.Id.ToString(), null, ipAddress, userAgent, now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthResult(AuthStatus.Success, ToResponse(issued));
    }

    public async Task<AuthResult> RefreshAsync(RefreshRequest request, string? ipAddress, string? userAgent, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var hash = tokenService.HashRefreshToken(request.RefreshToken);
        var existing = await dbContext.RefreshTokens
            .Include(x => x.User)
                .ThenInclude(x => x.UserRoles)
                    .ThenInclude(x => x.Role)
                        .ThenInclude(x => x.RolePermissions)
                            .ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);

        if (existing is null)
        {
            return new AuthResult(AuthStatus.InvalidRefreshToken);
        }

        if (existing.IsRevoked)
        {
            await RevokeAllActiveTokensAsync(existing.UserId, "refresh_token_reuse", ipAddress, now, cancellationToken);
            dbContext.AuditLogs.Add(CreateAudit(existing.UserId, "auth.refresh.reuse_detected", "User", existing.UserId.ToString(), null, ipAddress, userAgent, now));
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AuthResult(AuthStatus.InvalidRefreshToken);
        }

        if (existing.ExpiresAtUtc <= now || !existing.User.IsActive)
        {
            return new AuthResult(AuthStatus.AccountUnavailable);
        }

        var issued = IssueForUser(existing.User, now);
        var replacement = CreateRefreshToken(existing.UserId, issued, ipAddress, now);
        existing.RevokedAtUtc = now;
        existing.RevokedByIp = ipAddress;
        existing.RevokeReason = "rotated";
        existing.ReplacedByTokenId = replacement.Id;
        dbContext.RefreshTokens.Add(replacement);
        dbContext.AuditLogs.Add(CreateAudit(existing.UserId, "auth.refresh.rotated", "User", existing.UserId.ToString(), null, ipAddress, userAgent, now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthResult(AuthStatus.Success, ToResponse(issued));
    }

    public async Task RevokeAsync(Guid userId, string refreshToken, string? ipAddress, CancellationToken cancellationToken)
    {
        var hash = tokenService.HashRefreshToken(refreshToken);
        var token = await dbContext.RefreshTokens.SingleOrDefaultAsync(x => x.UserId == userId && x.TokenHash == hash, cancellationToken);
        if (token is null || token.IsRevoked)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        token.RevokedAtUtc = now;
        token.RevokedByIp = ipAddress;
        token.RevokeReason = "logout";
        dbContext.AuditLogs.Add(CreateAudit(userId, "auth.logout", "User", userId.ToString(), null, ipAddress, null, now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<User?> LoadUserAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        return await dbContext.Users
            .Include(x => x.UserRoles)
                .ThenInclude(x => x.Role)
                    .ThenInclude(x => x.RolePermissions)
                        .ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
    }

    private IssuedTokens IssueForUser(User user, DateTime now)
    {
        var activeRoles = user.UserRoles.Where(x => x.Role.IsActive).Select(x => x.Role).ToArray();
        var roles = activeRoles.Select(x => x.Name);
        var permissions = activeRoles.SelectMany(x => x.RolePermissions).Select(x => x.Permission.Code);
        return tokenService.Issue(user, roles, permissions, now);
    }

    private static RefreshToken CreateRefreshToken(Guid userId, IssuedTokens issued, string? ipAddress, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = issued.RefreshTokenHash,
        ExpiresAtUtc = issued.RefreshTokenExpiresAtUtc,
        CreatedAtUtc = now,
        CreatedByIp = ipAddress
    };

    private async Task RevokeAllActiveTokensAsync(Guid userId, string reason, string? ipAddress, DateTime now, CancellationToken cancellationToken)
    {
        var tokens = await dbContext.RefreshTokens
            .Where(x => x.UserId == userId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.RevokedAtUtc = now;
            token.RevokedByIp = ipAddress;
            token.RevokeReason = reason;
        }
    }

    private static AuditLog CreateAudit(Guid? actorUserId, string action, string targetType, string? targetId, string? reason, string? ipAddress, string? userAgent, DateTime now) => new()
    {
        ActorUserId = actorUserId,
        Action = action,
        TargetType = targetType,
        TargetId = targetId,
        MetadataJson = reason is null ? null : $"{{\"reason\":\"{reason}\"}}",
        IpAddress = ipAddress,
        UserAgent = userAgent,
        CreatedAtUtc = now
    };

    private static AuthResponse ToResponse(IssuedTokens issued) => new(
        issued.AccessToken,
        issued.AccessTokenExpiresAtUtc,
        issued.RefreshToken,
        issued.RefreshTokenExpiresAtUtc);

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();
}
