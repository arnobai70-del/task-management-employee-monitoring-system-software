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
[Authorize(Policy = PermissionCatalog.AccessAssignmentsRead)]
[Route("api/access-assignments")]
public sealed class AccessAssignmentsController(IAccessAssignmentService accessAssignmentService) : ControllerBase
{
    [HttpGet("rdp")]
    public async Task<ActionResult<PagedResponse<RdpAssignmentResponse>>> GetRdp(
        Guid? employeeId,
        bool? isActive,
        string? search,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await accessAssignmentService.GetRdpAssignmentsAsync(employeeId, isActive, search, page, pageSize, cancellationToken));

    [HttpPost("rdp")]
    [Authorize(Policy = PermissionCatalog.AccessAssignmentsManage)]
    public async Task<ActionResult<RdpAssignmentResponse>> CreateRdp(UpsertRdpAssignmentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await accessAssignmentService.CreateRdpAssignmentAsync(request, Actor(), cancellationToken));

    [HttpPut("rdp/{id:guid}")]
    [Authorize(Policy = PermissionCatalog.AccessAssignmentsManage)]
    public async Task<ActionResult<RdpAssignmentResponse>> UpdateRdp(Guid id, UpsertRdpAssignmentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await accessAssignmentService.UpdateRdpAssignmentAsync(id, request, Actor(), cancellationToken));

    [HttpGet("ip")]
    public async Task<ActionResult<PagedResponse<IpAssignmentResponse>>> GetIp(
        Guid? employeeId,
        IpAssignmentStatus? status,
        string? search,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await accessAssignmentService.GetIpAssignmentsAsync(employeeId, status, search, page, pageSize, cancellationToken));

    [HttpPost("ip")]
    [Authorize(Policy = PermissionCatalog.AccessAssignmentsManage)]
    public async Task<ActionResult<IpAssignmentResponse>> CreateIp(UpsertIpAssignmentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await accessAssignmentService.CreateIpAssignmentAsync(request, Actor(), cancellationToken));

    [HttpPut("ip/{id:guid}")]
    [Authorize(Policy = PermissionCatalog.AccessAssignmentsManage)]
    public async Task<ActionResult<IpAssignmentResponse>> UpdateIp(Guid id, UpsertIpAssignmentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await accessAssignmentService.UpdateIpAssignmentAsync(id, request, Actor(), cancellationToken));

    [HttpGet("websites")]
    public async Task<ActionResult<PagedResponse<WebsiteAssignmentResponse>>> GetWebsites(
        Guid? employeeId,
        bool? isActive,
        string? search,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await accessAssignmentService.GetWebsiteAssignmentsAsync(employeeId, isActive, search, page, pageSize, cancellationToken));

    [HttpPost("websites")]
    [Authorize(Policy = PermissionCatalog.AccessAssignmentsManage)]
    public async Task<ActionResult<WebsiteAssignmentResponse>> CreateWebsite(UpsertWebsiteAssignmentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await accessAssignmentService.CreateWebsiteAssignmentAsync(request, Actor(), cancellationToken));

    [HttpPut("websites/{id:guid}")]
    [Authorize(Policy = PermissionCatalog.AccessAssignmentsManage)]
    public async Task<ActionResult<WebsiteAssignmentResponse>> UpdateWebsite(Guid id, UpsertWebsiteAssignmentRequest request, CancellationToken cancellationToken)
        => ToActionResult(await accessAssignmentService.UpdateWebsiteAssignmentAsync(id, request, Actor(), cancellationToken));

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
