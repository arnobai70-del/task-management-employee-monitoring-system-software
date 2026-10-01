using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Route("api/security-observability")]
public sealed class SecurityObservabilityController(ISecurityObservabilityService service) : ControllerBase
{
    [HttpGet("dashboard")]
    [Authorize(Policy = PermissionCatalog.AuditRead)]
    public async Task<ActionResult<SecurityDashboardResponse>> GetDashboard(
        int? windowHours,
        CancellationToken cancellationToken)
        => Ok(await service.GetDashboardAsync(windowHours, cancellationToken));

    [HttpGet("audit-export.csv")]
    [Authorize(Policy = PermissionCatalog.AuditExport)]
    public async Task<IActionResult> ExportAudit(
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var export = await service.ExportAsync(fromUtc, toUtc, cancellationToken);
            Response.Headers["X-Audit-SHA256"] = export.Sha256;
            Response.Headers["X-Audit-Record-Count"] = export.RecordCount.ToString();
            Response.Headers["X-Audit-Truncated"] = export.Truncated ? "true" : "false";
            return File(export.Content, "text/csv; charset=utf-8", export.FileName);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ApiOperationError("invalid_export_range", ex.Message));
        }
    }
}
