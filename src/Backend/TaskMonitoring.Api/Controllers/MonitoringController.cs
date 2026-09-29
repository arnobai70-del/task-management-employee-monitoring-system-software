using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/monitoring")]
public sealed class MonitoringController(IMonitoringTelemetryService monitoringService) : ControllerBase
{
    [HttpGet("policy")]
    [Authorize(Policy = PermissionCatalog.MonitoringRead)]
    public async Task<ActionResult<MonitoringPolicyResponse>> Policy(CancellationToken cancellationToken)
        => Ok(await monitoringService.GetPolicyAsync(cancellationToken));

    [HttpPut("policy")]
    [Authorize(Policy = PermissionCatalog.MonitoringManage)]
    public async Task<ActionResult<MonitoringPolicyResponse>> UpdatePolicy(UpdateMonitoringPolicyRequest request, CancellationToken cancellationToken)
        => ToActionResult(await monitoringService.UpdatePolicyAsync(request, Actor(), cancellationToken));

    [HttpGet("applications")]
    [Authorize(Policy = PermissionCatalog.MonitoringRead)]
    public async Task<ActionResult<IReadOnlyCollection<ApprovedApplicationResponse>>> Applications(bool includeInactive = true, CancellationToken cancellationToken = default)
        => Ok(await monitoringService.GetApplicationsAsync(includeInactive, cancellationToken));

    [HttpPost("applications")]
    [Authorize(Policy = PermissionCatalog.MonitoringManage)]
    public async Task<ActionResult<ApprovedApplicationResponse>> CreateApplication(UpsertApprovedApplicationRequest request, CancellationToken cancellationToken)
        => ToActionResult(await monitoringService.CreateApplicationAsync(request, Actor(), cancellationToken));

    [HttpPut("applications/{id:guid}")]
    [Authorize(Policy = PermissionCatalog.MonitoringManage)]
    public async Task<ActionResult<ApprovedApplicationResponse>> UpdateApplication(Guid id, UpsertApprovedApplicationRequest request, CancellationToken cancellationToken)
        => ToActionResult(await monitoringService.UpdateApplicationAsync(id, request, Actor(), cancellationToken));

    [HttpGet("domains")]
    [Authorize(Policy = PermissionCatalog.MonitoringRead)]
    public async Task<ActionResult<IReadOnlyCollection<ApprovedBusinessDomainResponse>>> Domains(bool includeInactive = true, CancellationToken cancellationToken = default)
        => Ok(await monitoringService.GetDomainsAsync(includeInactive, cancellationToken));

    [HttpPost("domains")]
    [Authorize(Policy = PermissionCatalog.MonitoringManage)]
    public async Task<ActionResult<ApprovedBusinessDomainResponse>> CreateDomain(UpsertApprovedBusinessDomainRequest request, CancellationToken cancellationToken)
        => ToActionResult(await monitoringService.CreateDomainAsync(request, Actor(), cancellationToken));

    [HttpPut("domains/{id:guid}")]
    [Authorize(Policy = PermissionCatalog.MonitoringManage)]
    public async Task<ActionResult<ApprovedBusinessDomainResponse>> UpdateDomain(Guid id, UpsertApprovedBusinessDomainRequest request, CancellationToken cancellationToken)
        => ToActionResult(await monitoringService.UpdateDomainAsync(id, request, Actor(), cancellationToken));

    [HttpGet("activity")]
    [Authorize(Policy = PermissionCatalog.MonitoringRead)]
    public async Task<ActionResult<PagedResponse<MonitoringActivityResponse>>> Activity(
        Guid? employeeId = null,
        MonitoringActivityKind? kind = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        string? search = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => ToActionResult(await monitoringService.GetActivityAsync(employeeId, kind, fromUtc, toUtc, search, page, pageSize, cancellationToken));

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
