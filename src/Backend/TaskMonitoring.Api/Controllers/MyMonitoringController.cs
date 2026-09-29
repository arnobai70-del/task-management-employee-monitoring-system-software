using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/me/monitoring")]
public sealed class MyMonitoringController(IMonitoringTelemetryService monitoringService) : ControllerBase
{
    [HttpGet("policy")]
    public async Task<ActionResult<EmployeeMonitoringPolicyResponse>> Policy(CancellationToken cancellationToken)
        => ToActionResult(await monitoringService.GetMyPolicyAsync(Actor(), cancellationToken));

    [HttpPost("application-activity")]
    public async Task<ActionResult<MonitoringIngestResponse>> ApplicationActivity(RecordApplicationActivityRequest request, CancellationToken cancellationToken)
        => ToActionResult(await monitoringService.RecordApplicationAsync(Actor(), request, cancellationToken));

    [HttpPost("business-domain-activity")]
    public async Task<ActionResult<MonitoringIngestResponse>> BusinessDomainActivity(RecordBusinessDomainActivityRequest request, CancellationToken cancellationToken)
        => ToActionResult(await monitoringService.RecordDomainAsync(Actor(), request, cancellationToken));

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
