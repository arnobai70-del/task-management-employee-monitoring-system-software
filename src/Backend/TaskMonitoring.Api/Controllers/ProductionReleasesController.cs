using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize(Policy = PermissionCatalog.ReportsRead)]
[Route("api/production-releases")]
public sealed class ProductionReleasesController(
    AppDbContext dbContext,
    IOperationsHealthService operationsHealthService,
    IOperationsIncidentService operationsIncidentService,
    ISecurityAlertService securityAlertService,
    IAgentUpdateService agentUpdateService,
    IOptions<OperationsOptions> operationsOptions,
    TimeProvider timeProvider) : ControllerBase
{
    private readonly IProductionReleaseControlService releaseControlService = new ProductionReleaseControlService(
        dbContext,
        operationsHealthService,
        operationsIncidentService,
        securityAlertService,
        agentUpdateService,
        operationsOptions,
        timeProvider);

    [HttpGet("dashboard")]
    public async Task<ActionResult<ProductionReleaseDashboardResponse>> GetDashboard(CancellationToken cancellationToken)
        => Ok(await releaseControlService.GetDashboardAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProductionReleaseResponse>> GetById(Guid id, CancellationToken cancellationToken)
        => ToActionResult(await releaseControlService.GetByIdAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = PermissionCatalog.ProductionReleasesManage)]
    public async Task<ActionResult<ProductionReleaseResponse>> Register(
        ProductionReleaseRegisterRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await releaseControlService.RegisterAsync(request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = PermissionCatalog.ProductionReleasesManage)]
    public async Task<ActionResult<ProductionReleaseResponse>> Approve(
        Guid id,
        ProductionReleaseActionRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await releaseControlService.ApproveAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/authorize-promotion")]
    [Authorize(Policy = PermissionCatalog.ProductionReleasesManage)]
    public async Task<ActionResult<ProductionReleaseResponse>> AuthorizePromotion(
        Guid id,
        ProductionReleaseActionRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await releaseControlService.AuthorizePromotionAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/verify-deployment")]
    [Authorize(Policy = PermissionCatalog.ProductionReleasesManage)]
    public async Task<ActionResult<ProductionReleaseResponse>> VerifyDeployment(
        Guid id,
        ProductionReleaseActionRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await releaseControlService.VerifyDeploymentAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/request-rollback")]
    [Authorize(Policy = PermissionCatalog.ProductionReleasesManage)]
    public async Task<ActionResult<ProductionReleaseResponse>> RequestRollback(
        Guid id,
        ProductionReleaseActionRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await releaseControlService.RequestRollbackAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/verify-rollback")]
    [Authorize(Policy = PermissionCatalog.ProductionReleasesManage)]
    public async Task<ActionResult<ProductionReleaseResponse>> VerifyRollback(
        Guid id,
        ProductionReleaseActionRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await releaseControlService.VerifyRollbackAsync(id, request, Actor(), cancellationToken));

    [HttpPost("{id:guid}/withdraw")]
    [Authorize(Policy = PermissionCatalog.ProductionReleasesManage)]
    public async Task<ActionResult<ProductionReleaseResponse>> Withdraw(
        Guid id,
        ProductionReleaseActionRequest request,
        CancellationToken cancellationToken)
        => ToActionResult(await releaseControlService.WithdrawAsync(id, request, Actor(), cancellationToken));

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
