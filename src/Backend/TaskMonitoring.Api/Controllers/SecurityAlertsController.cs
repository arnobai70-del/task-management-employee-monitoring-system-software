using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize(Policy = PermissionCatalog.AuditRead)]
[Route("api/security-alerts")]
public sealed class SecurityAlertsController(ISecurityAlertService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<SecurityAlertResponse>>> GetAll(
        SecurityAlertStatus? status = null,
        SecurityAlertSeverity? severity = null,
        SecurityAlertKind? kind = null,
        string? search = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await service.GetAsync(status, severity, kind, search, page, pageSize, cancellationToken));

    [HttpGet("summary")]
    public async Task<ActionResult<SecurityAlertSummaryResponse>> GetSummary(CancellationToken cancellationToken)
        => Ok(await service.GetSummaryAsync(cancellationToken));

    [HttpGet("assignees")]
    [Authorize(Policy = PermissionCatalog.SecurityAlertsManage)]
    public async Task<ActionResult<IReadOnlyCollection<SecurityAlertAssigneeResponse>>> GetAssignees(CancellationToken cancellationToken)
        => Ok(await service.GetAssigneesAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SecurityAlertResponse>> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await service.GetByIdAsync(id, cancellationToken));

    [HttpPost("{id:guid}/acknowledge")]
    [Authorize(Policy = PermissionCatalog.SecurityAlertsManage)]
    public async Task<ActionResult<SecurityAlertResponse>> Acknowledge(
        Guid id,
        SecurityAlertAcknowledgeRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await service.AcknowledgeAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/assign")]
    [Authorize(Policy = PermissionCatalog.SecurityAlertsManage)]
    public async Task<ActionResult<SecurityAlertResponse>> Assign(
        Guid id,
        SecurityAlertAssignRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await service.AssignAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/resolve")]
    [Authorize(Policy = PermissionCatalog.SecurityAlertsManage)]
    public async Task<ActionResult<SecurityAlertResponse>> Resolve(
        Guid id,
        SecurityAlertResolveRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await service.ResolveAsync(id, request, Actor(), cancellationToken));

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
        if (result.Status == OperationStatus.Success && result.Value is not null) return Ok(result.Value);

        var error = new ApiOperationError(result.ErrorCode ?? "request_failed", result.Message ?? "The request could not be completed.");
        return result.Status switch
        {
            OperationStatus.NotFound => NotFound(error),
            OperationStatus.Conflict => Conflict(error),
            _ => BadRequest(error)
        };
    }
}
