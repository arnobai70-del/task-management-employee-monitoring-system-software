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
public sealed class MyRealtimeController(IRealtimeWorkspaceService realtimeWorkspaceService) : ControllerBase
{
    [HttpPost("presence/heartbeat")]
    public async Task<ActionResult<EmployeePresenceResponse>> Heartbeat(
        PresenceHeartbeatRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await realtimeWorkspaceService.RecordHeartbeatAsync(Actor(), request, cancellationToken));

    [HttpGet("notifications")]
    public async Task<ActionResult<PagedResponse<EmployeeNotificationResponse>>> Notifications(
        bool unreadOnly = false,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => ToActionResult(await realtimeWorkspaceService.GetMyNotificationsAsync(Actor(), unreadOnly, page, pageSize, cancellationToken));

    [HttpPost("notifications/{notificationId:guid}/read")]
    public async Task<ActionResult<EmployeeNotificationResponse>> MarkRead(Guid notificationId, CancellationToken cancellationToken)
        => ToActionResult(await realtimeWorkspaceService.MarkMyNotificationReadAsync(Actor(), notificationId, cancellationToken));

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
