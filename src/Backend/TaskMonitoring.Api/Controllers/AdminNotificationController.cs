using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize(Policy = PermissionCatalog.TasksManage)]
[Route("api/admin-notifications")]
public sealed class AdminNotificationController(
    IAdminNotificationService notificationService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<AdminNotificationResponse>>> GetMine(
        bool unreadOnly = false,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => ToActionResult(await notificationService.GetMineAsync(
            Actor(),
            unreadOnly,
            page,
            pageSize,
            cancellationToken));

    [HttpGet("summary")]
    public async Task<ActionResult<AdminNotificationSummaryResponse>> GetSummary(
        CancellationToken cancellationToken)
        => ToActionResult(await notificationService.GetSummaryAsync(Actor(), cancellationToken));

    [HttpPost("{notificationId:guid}/read")]
    public async Task<ActionResult<AdminNotificationResponse>> MarkRead(
        Guid notificationId,
        CancellationToken cancellationToken)
        => ToActionResult(await notificationService.MarkReadAsync(
            Actor(),
            notificationId,
            cancellationToken));

    [HttpPost("read-all")]
    public async Task<ActionResult<AdminNotificationMarkAllReadResponse>> MarkAllRead(
        CancellationToken cancellationToken)
        => ToActionResult(await notificationService.MarkAllReadAsync(Actor(), cancellationToken));

    private RequestActor Actor()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return new RequestActor(
            Guid.TryParse(subject, out var userId) ? userId : null,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString());
    }

    private ActionResult<T> ToActionResult<T>(OperationResult<T> result)
    {
        if (result.Status == OperationStatus.Success && result.Value is not null)
        {
            return Ok(result.Value);
        }

        var error = new ApiOperationError(
            result.ErrorCode ?? "request_failed",
            result.Message ?? "The request could not be completed.");
        return result.Status switch
        {
            OperationStatus.NotFound => NotFound(error),
            OperationStatus.Conflict => Conflict(error),
            _ => BadRequest(error)
        };
    }
}
