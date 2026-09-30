using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/me/agent-health")]
public sealed class MyAgentHealthController(
    IOperationsHealthService operationsHealthService,
    IAgentUpdateService agentUpdateService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<AgentHealthReportResponse>> Record(
        AgentHealthReportRequest request,
        CancellationToken cancellationToken)
    {
        var actor = Actor();
        var result = await operationsHealthService.RecordAgentHealthAsync(actor, request, cancellationToken);
        if (result.Status == OperationStatus.Success && request.DeviceId.HasValue)
        {
            await agentUpdateService.ObserveDeviceAsync(
                actor,
                request.DeviceId.Value,
                request.MachineName,
                request.InstalledVersion,
                request.UpdaterVersion,
                cancellationToken);
        }
        return ToActionResult(result);
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
