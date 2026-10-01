using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Route("api/employees")]
public sealed class EmployeesController(
    IEmployeeCoreService employeeCoreService,
    AppDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionCatalog.EmployeesRead)]
    public async Task<ActionResult<PagedResponse<EmployeeResponse>>> GetAll(
        string? search,
        Guid? departmentId,
        bool? isActive,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await employeeCoreService.GetEmployeesAsync(search, departmentId, isActive, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.EmployeesRead)]
    public async Task<ActionResult<EmployeeResponse>> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await employeeCoreService.GetEmployeeAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.EmployeesManage)]
    [Authorize(Policy = PermissionCatalog.RolesManage)]
    public async Task<ActionResult<EmployeeResponse>> Create(CreateEmployeeRequest request, CancellationToken cancellationToken)
    {
        var result = await employeeCoreService.CreateEmployeeAsync(request, Actor(), cancellationToken);
        return result.Status == OperationStatus.Success && result.Value is not null
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : ToActionResult(result);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.EmployeesManage)]
    [Authorize(Policy = PermissionCatalog.RolesManage)]
    public async Task<ActionResult<EmployeeResponse>> Update(Guid id, UpdateEmployeeRequest request, CancellationToken cancellationToken)
        => ToActionResult(await employeeCoreService.UpdateEmployeeAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/reset-password")]
    [Authorize(Policy = PermissionCatalog.EmployeesManage)]
    [Authorize(Policy = PermissionCatalog.RolesManage)]
    public async Task<IActionResult> ResetPassword(Guid id, ResetEmployeePasswordRequest request, CancellationToken cancellationToken)
    {
        var employee = await dbContext.Employees
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (employee is null)
        {
            return NotFound(new ApiOperationError("employee_not_found", "Employee was not found."));
        }

        var actor = Actor();
        var targetIsSuperAdmin = await dbContext.UserRoles
            .AnyAsync(x => x.UserId == employee.UserId && x.Role.Name == "SuperAdmin", cancellationToken);
        if (targetIsSuperAdmin && actor.UserId != employee.UserId)
        {
            var actorIsSuperAdmin = actor.UserId.HasValue && await dbContext.UserRoles
                .AnyAsync(x => x.UserId == actor.UserId.Value && x.Role.Name == "SuperAdmin", cancellationToken);
            if (!actorIsSuperAdmin)
            {
                return Forbid();
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        employee.User.PasswordHash = passwordHasher.HashPassword(employee.User, request.NewPassword);
        employee.User.FailedLoginAttempts = 0;
        employee.User.LockoutEndUtc = null;
        employee.User.UpdatedAtUtc = now;

        var activeRefreshTokens = await dbContext.RefreshTokens
            .Where(x => x.UserId == employee.UserId && x.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var token in activeRefreshTokens)
        {
            token.RevokedAtUtc = now;
            token.RevokedByIp = actor.IpAddress;
            token.RevokeReason = "employee.password_reset";
        }

        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = "employee.password_reset",
            TargetType = "Employee",
            TargetId = employee.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                employee.User.Email,
                RefreshTokensRevoked = activeRefreshTokens.Count
            }),
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CreatedAtUtc = now
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private ActionResult<T> ToActionResult<T>(OperationResult<T> result)
    {
        if (result.Status == OperationStatus.Success && result.Value is not null)
        {
            return Ok(result.Value);
        }

        var error = new ApiOperationError(result.ErrorCode ?? "request_failed", result.Message ?? "The request could not be completed.");
        return result.Status switch
        {
            OperationStatus.NotFound => NotFound(error),
            OperationStatus.Conflict => Conflict(error),
            _ => BadRequest(error)
        };
    }

    private RequestActor Actor()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return new RequestActor(
            Guid.TryParse(subject, out var userId) ? userId : null,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString());
    }
}

public sealed class ResetEmployeePasswordRequest
{
    [Required, StringLength(256, MinimumLength = 12)]
    public string NewPassword { get; init; } = string.Empty;
}
