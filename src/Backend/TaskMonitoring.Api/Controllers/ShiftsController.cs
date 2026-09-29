using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Route("api/shifts")]
public sealed class ShiftsController(IAttendanceCoreService attendanceCoreService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionCatalog.ShiftsRead)]
    public async Task<ActionResult<IReadOnlyCollection<ShiftResponse>>> GetAll(bool? isActive, CancellationToken cancellationToken)
        => Ok(await attendanceCoreService.GetShiftsAsync(isActive, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.ShiftsManage)]
    public async Task<ActionResult<ShiftResponse>> Create(CreateShiftRequest request, CancellationToken cancellationToken)
        => ToActionResult(await attendanceCoreService.CreateShiftAsync(request, Actor(), cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionCatalog.ShiftsManage)]
    public async Task<ActionResult<ShiftResponse>> Update(Guid id, UpdateShiftRequest request, CancellationToken cancellationToken)
        => ToActionResult(await attendanceCoreService.UpdateShiftAsync(id, request, Actor(), cancellationToken));

    [HttpGet("assignments")]
    [Authorize(Policy = PermissionCatalog.ShiftsRead)]
    public async Task<ActionResult<IReadOnlyCollection<ShiftAssignmentResponse>>> GetAssignments(Guid? employeeId, CancellationToken cancellationToken)
        => Ok(await attendanceCoreService.GetShiftAssignmentsAsync(employeeId, cancellationToken));

    [HttpPost("assignments")]
    [Authorize(Policy = PermissionCatalog.ShiftsManage)]
    public async Task<ActionResult<ShiftAssignmentResponse>> CreateAssignment(CreateShiftAssignmentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await attendanceCoreService.CreateShiftAssignmentAsync(request, Actor(), cancellationToken));

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
