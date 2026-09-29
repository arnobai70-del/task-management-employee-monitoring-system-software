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
[Route("api/attendance")]
public sealed class AttendanceController(IAttendanceCoreService attendanceCoreService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionCatalog.AttendanceRead)]
    public async Task<ActionResult<PagedResponse<WorkSessionResponse>>> GetAll(
        Guid? employeeId,
        DateOnly? from,
        DateOnly? to,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await attendanceCoreService.GetAttendanceAsync(employeeId, from, to, page, pageSize, cancellationToken));

    [HttpGet("me/status")]
    public async Task<ActionResult<AttendanceStateResponse>> GetMyStatus(CancellationToken cancellationToken)
        => ToActionResult(await attendanceCoreService.GetMyStatusAsync(Actor(), cancellationToken));

    [HttpPost("me/check-in")]
    public async Task<ActionResult<WorkSessionResponse>> CheckIn(CancellationToken cancellationToken)
        => ToActionResult(await attendanceCoreService.CheckInAsync(Actor(), cancellationToken));

    [HttpPost("me/breaks/start")]
    public async Task<ActionResult<WorkSessionResponse>> StartBreak(CancellationToken cancellationToken)
        => ToActionResult(await attendanceCoreService.StartBreakAsync(Actor(), cancellationToken));

    [HttpPost("me/breaks/end")]
    public async Task<ActionResult<WorkSessionResponse>> EndBreak(CancellationToken cancellationToken)
        => ToActionResult(await attendanceCoreService.EndBreakAsync(Actor(), cancellationToken));

    [HttpPost("me/check-out")]
    public async Task<ActionResult<WorkSessionResponse>> CheckOut(CancellationToken cancellationToken)
        => ToActionResult(await attendanceCoreService.CheckOutAsync(Actor(), cancellationToken));

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
