using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Controllers;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class EmployeePasswordResetControllerTests
{
    [Fact]
    public async Task Reset_password_hashes_new_password_clears_lockout_revokes_refresh_tokens_and_audits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var hasher = new PasswordHasher<User>();
        var actorUserId = Guid.NewGuid();
        var user = new User
        {
            Email = "employee@example.com",
            NormalizedEmail = AuthService.NormalizeEmail("employee@example.com"),
            FailedLoginAttempts = 4,
            LockoutEndUtc = DateTime.UtcNow.AddMinutes(10)
        };
        user.PasswordHash = hasher.HashPassword(user, "Original-Password-123");
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = "EMP-RESET",
            NormalizedEmployeeCode = "EMP-RESET",
            FullName = "Reset Employee",
            NormalizedFullName = "RESET EMPLOYEE",
            JobTitle = "Employee"
        };
        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            User = user,
            TokenHash = new string('a', 64),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        };
        db.AddRange(user, employee, refreshToken);
        await db.SaveChangesAsync(cancellationToken);

        var employeeCoreService = new EmployeeCoreService(db, hasher, TimeProvider.System);
        var controller = new EmployeesController(employeeCoreService, db, hasher, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext(actorUserId) }
        };

        var result = await controller.ResetPassword(
            employee.Id,
            new ResetEmployeePasswordRequest { NewPassword = "Replacement-Password-456" },
            cancellationToken);

        Assert.IsType<NoContentResult>(result);
        var updatedUser = await db.Users.SingleAsync(cancellationToken);
        Assert.NotEqual("Replacement-Password-456", updatedUser.PasswordHash);
        Assert.Equal(
            PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(updatedUser, updatedUser.PasswordHash, "Replacement-Password-456"));
        Assert.Equal(0, updatedUser.FailedLoginAttempts);
        Assert.Null(updatedUser.LockoutEndUtc);

        var updatedToken = await db.RefreshTokens.SingleAsync(cancellationToken);
        Assert.True(updatedToken.IsRevoked);
        Assert.Equal("employee.password_reset", updatedToken.RevokeReason);
        Assert.Equal(IPAddress.Loopback.ToString(), updatedToken.RevokedByIp);

        var audit = await db.AuditLogs.SingleAsync(x => x.Action == "employee.password_reset", cancellationToken);
        Assert.Equal(actorUserId, audit.ActorUserId);
        Assert.Equal("Employee", audit.TargetType);
        Assert.Equal(employee.Id.ToString(), audit.TargetId);
        Assert.DoesNotContain("Replacement-Password-456", audit.MetadataJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_password_contract_requires_twelve_characters()
    {
        var request = new ResetEmployeePasswordRequest { NewPassword = "short" };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ResetEmployeePasswordRequest.NewPassword)));
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static DefaultHttpContext CreateHttpContext(Guid actorUserId)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers.UserAgent = "tests";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, actorUserId.ToString())],
            authenticationType: "tests"));
        return context;
    }
}
