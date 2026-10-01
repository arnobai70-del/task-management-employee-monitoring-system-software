using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/agent-updates/device")]
public sealed class AgentUpdateDeviceController(IAgentUpdateService service) : ControllerBase
{
    [HttpPost("register")]
    [EnableRateLimiting("agent-enrollment")]
    public async Task<ActionResult<AgentUpdateDeviceRegisterResponse>> Register(
        AgentUpdateDeviceRegisterRequest request,
        CancellationToken cancellationToken)
        => ToDeviceActionResult(await service.RegisterDeviceAsync(
            request,
            Request.Headers["X-Agent-Enrollment-Key"].ToString(),
            cancellationToken));

    [HttpGet("{deviceId:guid}/plan")]
    [EnableRateLimiting("agent-device")]
    public async Task<ActionResult<AgentUpdateDevicePlanResponse>> GetPlan(
        Guid deviceId,
        CancellationToken cancellationToken)
        => ToDeviceActionResult(await service.GetDevicePlanAsync(
            deviceId,
            Request.Headers["X-Agent-Device-Token"].ToString(),
            cancellationToken));

    [HttpPost("{deviceId:guid}/status")]
    [EnableRateLimiting("agent-device")]
    public async Task<ActionResult<AgentUpdateDeviceStatusResponse>> RecordStatus(
        Guid deviceId,
        AgentUpdateDeviceStatusRequest request,
        CancellationToken cancellationToken)
        => ToDeviceActionResult(await service.RecordDeviceStatusAsync(
            deviceId,
            Request.Headers["X-Agent-Device-Token"].ToString(),
            request,
            cancellationToken));

    private ActionResult<T> ToDeviceActionResult<T>(OperationResult<T> result)
    {
        if (result.Status == OperationStatus.Success && result.Value is not null)
        {
            return Ok(result.Value);
        }

        var error = new ApiOperationError(result.ErrorCode ?? "request_failed", result.Message ?? "The request could not be completed.");
        if ((result.ErrorCode?.Contains("auth", StringComparison.Ordinal) ?? false) ||
            string.Equals(result.ErrorCode, "agent_update_enrollment_invalid", StringComparison.Ordinal))
        {
            return Unauthorized(error);
        }

        return result.Status switch
        {
            OperationStatus.NotFound => NotFound(error),
            OperationStatus.Conflict => Conflict(error),
            _ => BadRequest(error)
        };
    }
}
