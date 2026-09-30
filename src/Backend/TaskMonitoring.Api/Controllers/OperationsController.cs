using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize(Policy = PermissionCatalog.ReportsRead)]
[Route("api/operations")]
public sealed class OperationsController(IOperationsHealthService operationsHealthService) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<OperationsOverviewResponse>> GetOverview(
        string? search = null,
        Guid? departmentId = null,
        string? health = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
        => Ok(await operationsHealthService.GetOverviewAsync(search, departmentId, health, limit, cancellationToken));
}
