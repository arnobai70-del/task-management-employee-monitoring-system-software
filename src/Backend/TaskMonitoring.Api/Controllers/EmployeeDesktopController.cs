using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/desktop")]
public sealed class EmployeeDesktopController(IEmployeeDesktopService employeeDesktopService) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<EmployeeDesktopDashboardResponse>> GetMyDashboard(CancellationToken cancellationToken)
        => ToActionResult(await employeeDesktopService.GetDashboardAsync(Actor(), cancellationToken));

    [HttpPost("me/heartbeat")]
    public async Task<ActionResult<DesktopHeartbeatResponse>> Heartbeat(
        DesktopHeartbeatRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await employeeDesktopService.RecordHeartbeatAsync(request, Actor(), cancellationToken));

    [HttpGet("presence")]
    [Authorize(Policy = PermissionCatalog.EmployeesRead)]
    public async Task<ActionResult<PagedResponse<EmployeePresenceResponse>>> GetPresence(
        bool? online,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await employeeDesktopService.GetPresenceAsync(online, page, pageSize, cancellationToken));

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
