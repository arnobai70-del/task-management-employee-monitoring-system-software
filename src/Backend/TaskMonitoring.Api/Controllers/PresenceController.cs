using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Authorize(Policy = PermissionCatalog.PresenceRead)]
[Route("api/presence")]
public sealed class PresenceController(IRealtimeWorkspaceService realtimeWorkspaceService) : ControllerBase
{
    [HttpGet]
    public Task<PagedResponse<EmployeePresenceResponse>> GetPresence(
        string? search = null,
        bool? online = null,
        int page = 1,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
        => realtimeWorkspaceService.GetPresenceRosterAsync(search, online, page, pageSize, cancellationToken);
}
