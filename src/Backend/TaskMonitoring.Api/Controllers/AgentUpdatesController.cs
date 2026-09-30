using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize(Policy = PermissionCatalog.ReportsRead)]
[Route("api/agent-updates")]
public sealed class AgentUpdatesController(IAgentUpdateService service) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<AgentUpdateOverviewResponse>> GetOverview(CancellationToken cancellationToken)
        => Ok(await service.GetOverviewAsync(cancellationToken));

    [HttpPost("rollouts")]
    [Authorize(Policy = PermissionCatalog.AgentUpdatesManage)]
    public async Task<ActionResult<AgentUpdateRolloutResponse>> Create(
        AgentUpdateCreateRolloutRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await service.CreateRolloutAsync(request, Actor(), cancellationToken));

    [HttpPost("rollouts/{id:guid}/pause")]
    [Authorize(Policy = PermissionCatalog.AgentUpdatesManage)]
    public async Task<ActionResult<AgentUpdateRolloutResponse>> Pause(
        Guid id,
        AgentUpdateRolloutActionRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await service.PauseAsync(id, request, Actor(), cancellationToken));

    [HttpPost("rollouts/{id:guid}/resume")]
    [Authorize(Policy = PermissionCatalog.AgentUpdatesManage)]
    public async Task<ActionResult<AgentUpdateRolloutResponse>> Resume(
        Guid id,
        AgentUpdateRolloutActionRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await service.ResumeAsync(id, request, Actor(), cancellationToken));

    [HttpPost("rollouts/{id:guid}/cancel")]
    [Authorize(Policy = PermissionCatalog.AgentUpdatesManage)]
    public async Task<ActionResult<AgentUpdateRolloutResponse>> Cancel(
        Guid id,
        AgentUpdateRolloutActionRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await service.CancelAsync(id, request, Actor(), cancellationToken));

    [HttpPost("rollouts/{id:guid}/promote")]
    [Authorize(Policy = PermissionCatalog.AgentUpdatesManage)]
    public async Task<ActionResult<AgentUpdateRolloutResponse>> Promote(
        Guid id,
        AgentUpdatePromoteRolloutRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await service.PromoteAsync(id, request, Actor(), cancellationToken));

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

        var error = new ApiOperationError(result.ErrorCode ?? "request_failed", result.Message ?? "The request could not be completed.");
        return result.Status switch
        {
            OperationStatus.NotFound => NotFound(error),
            OperationStatus.Conflict => Conflict(error),
            _ => BadRequest(error)
        };
    }
}
