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
public sealed class AccessAssignmentLookupsController(AppDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<AccessEmployeeOptionResponse>>> GetEmployees(CancellationToken cancellationToken)
        => Ok(await dbContext.Employees
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.NormalizedFullName)
            .Select(x => new AccessEmployeeOptionResponse(x.Id, x.EmployeeCode, x.FullName))
            .ToListAsync(cancellationToken));
}
