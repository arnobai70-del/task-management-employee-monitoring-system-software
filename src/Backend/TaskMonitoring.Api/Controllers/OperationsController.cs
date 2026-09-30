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
[Route("api/operations")]
public sealed class OperationsController(
    IOperationsHealthService operationsHealthService,
    IOperationsIncidentService operationsIncidentService) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<OperationsOverviewResponse>> GetOverview(
        string? search = null,
        Guid? departmentId = null,
        string? health = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
        => Ok(await operationsHealthService.GetOverviewAsync(search, departmentId, health, limit, cancellationToken));

    [HttpGet("incidents")]
    public async Task<ActionResult<PagedResponse<OperationsIncidentResponse>>> GetIncidents(
        OperationsIncidentStatus? status = null,
        OperationsIncidentSeverity? severity = null,
        OperationsIncidentKind? kind = null,
        string? search = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await operationsIncidentService.GetAsync(status, severity, kind, search, page, pageSize, cancellationToken));

    [HttpGet("incidents/summary")]
    public async Task<ActionResult<OperationsIncidentSummaryResponse>> GetIncidentSummary(CancellationToken cancellationToken)
        => Ok(await operationsIncidentService.GetSummaryAsync(cancellationToken));

    [HttpGet("incidents/assignees")]
    [Authorize(Policy = PermissionCatalog.OperationsManage)]
    public async Task<ActionResult<IReadOnlyCollection<OperationsIncidentAssigneeResponse>>> GetIncidentAssignees(CancellationToken cancellationToken)
        => Ok(await operationsIncidentService.GetAssigneesAsync(cancellationToken));

    [HttpGet("incidents/{id:guid}")]
    public async Task<ActionResult<OperationsIncidentResponse>> GetIncident(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await operationsIncidentService.GetByIdAsync(id, cancellationToken));

    [HttpPost("incidents/{id:guid}/acknowledge")]
    [Authorize(Policy = PermissionCatalog.OperationsManage)]
    public async Task<ActionResult<OperationsIncidentResponse>> Acknowledge(
        Guid id,
        OperationsIncidentAcknowledgeRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await operationsIncidentService.AcknowledgeAsync(id, request, Actor(), cancellationToken));

    [HttpPost("incidents/{id:guid}/assign")]
    [Authorize(Policy = PermissionCatalog.OperationsManage)]
    public async Task<ActionResult<OperationsIncidentResponse>> Assign(
        Guid id,
        OperationsIncidentAssignRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await operationsIncidentService.AssignAsync(id, request, Actor(), cancellationToken));

    [HttpPost("incidents/{id:guid}/resolve")]
    [Authorize(Policy = PermissionCatalog.OperationsManage)]
    public async Task<ActionResult<OperationsIncidentResponse>> Resolve(
        Guid id,
        OperationsIncidentResolveRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await operationsIncidentService.ResolveAsync(id, request, Actor(), cancellationToken));

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
