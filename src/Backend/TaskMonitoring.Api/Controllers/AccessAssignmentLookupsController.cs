using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Security;

namespace TaskMonitoring.Api.Controllers;

public sealed record AccessEmployeeOptionResponse(Guid Id, string EmployeeCode, string FullName);

[ApiController]
[Authorize(Policy = PermissionCatalog.AccessAssignmentsRead)]
[Route("api/access-assignments/employees")]
public sealed class AccessAssignmentLookupsController(AppDbContext dbContext, ITeamScope teamScope) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<AccessEmployeeOptionResponse>>> GetEmployees(CancellationToken cancellationToken)
    {
        // Only employees the caller may assign to (G-27).
        var scope = await teamScope.GetVisibleEmployeeIdsAsync(cancellationToken);
        return Ok(await dbContext.Employees
            .AsNoTracking()
            .Where(x => x.IsActive && (scope == null || scope.Contains(x.Id)))
            .OrderBy(x => x.NormalizedFullName)
            .Select(x => new AccessEmployeeOptionResponse(x.Id, x.EmployeeCode, x.FullName))
            .ToListAsync(cancellationToken));
    }
}
