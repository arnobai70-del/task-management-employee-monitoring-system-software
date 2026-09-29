using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Controllers;

[ApiController]
[Route("api/roles")]
public sealed class RolesController(IEmployeeCoreService employeeCoreService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionCatalog.RolesRead)]
    public async Task<ActionResult<IReadOnlyCollection<RoleResponse>>> GetAll(bool activeOnly = true, CancellationToken cancellationToken = default)
        => Ok(await employeeCoreService.GetRolesAsync(activeOnly, cancellationToken));
}
