using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Route("api/audit-logs")]
public sealed class AuditLogsController(IAuditLogService auditLogService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionCatalog.AuditRead)]
    public async Task<ActionResult<PagedResponse<AuditLogResponse>>> GetAll(
        string? search,
        string? action,
        string? targetType,
        Guid? actorUserId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value)
        {
            return BadRequest(new ApiOperationError("invalid_date_range", "fromUtc must be earlier than or equal to toUtc."));
        }

        return Ok(await auditLogService.GetAuditLogsAsync(
            search,
            action,
            targetType,
            actorUserId,
            fromUtc,
            toUtc,
            page,
            pageSize,
            cancellationToken));
    }
}
