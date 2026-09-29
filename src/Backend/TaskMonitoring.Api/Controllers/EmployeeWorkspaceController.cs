using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/me")]
public sealed class EmployeeWorkspaceController(IEmployeeWorkspaceService employeeWorkspaceService) : ControllerBase
{
    [HttpGet("tasks")]
    public async Task<ActionResult<PagedResponse<ProjectTaskResponse>>> GetMyTasks(
        bool includeClosed = false,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => ToActionResult(await employeeWorkspaceService.GetMyTasksAsync(Actor(), includeClosed, page, pageSize, cancellationToken));

    [HttpGet("access")]
    public async Task<ActionResult<EmployeeAccessWorkspaceResponse>> GetMyAccess(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
        => ToActionResult(await employeeWorkspaceService.GetMyAccessAsync(Actor(), includeInactive, cancellationToken));

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
