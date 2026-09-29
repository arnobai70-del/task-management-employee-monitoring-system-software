using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Security;

namespace TaskMonitoring.Api.Controllers;

public sealed record ManagementEmployeeOption(Guid Id, string EmployeeCode, string FullName);
public sealed record ManagementDepartmentOption(Guid Id, string Code, string Name);
public sealed record ManagementRoleOption(Guid Id, string Name);
public sealed record ManagementProjectOption(Guid Id, string Code, string Name, string Status);

[ApiController]
[Route("api/admin-lookups")]
public sealed class AdminLookupController(AppDbContext dbContext) : ControllerBase
{
    [HttpGet("employees/departments")]
    [Authorize(Policy = PermissionCatalog.EmployeesRead)]
    public async Task<ActionResult<IReadOnlyCollection<ManagementDepartmentOption>>> EmployeeDepartments(CancellationToken cancellationToken)
        => Ok(await dbContext.Departments.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.NormalizedName)
            .Select(x => new ManagementDepartmentOption(x.Id, x.Code, x.Name))
            .ToListAsync(cancellationToken));

    [HttpGet("employees/roles")]
    [Authorize(Policy = PermissionCatalog.EmployeesRead)]
    public async Task<ActionResult<IReadOnlyCollection<ManagementRoleOption>>> EmployeeRoles(CancellationToken cancellationToken)
        => Ok(await dbContext.Roles.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new ManagementRoleOption(x.Id, x.Name))
            .ToListAsync(cancellationToken));

    [HttpGet("employees/supervisors")]
    [Authorize(Policy = PermissionCatalog.EmployeesRead)]
    public async Task<ActionResult<IReadOnlyCollection<ManagementEmployeeOption>>> EmployeeSupervisors(CancellationToken cancellationToken)
        => Ok(await ActiveEmployeeOptions(cancellationToken));

    [HttpGet("shifts/employees")]
    [Authorize(Policy = PermissionCatalog.ShiftsRead)]
    public async Task<ActionResult<IReadOnlyCollection<ManagementEmployeeOption>>> ShiftEmployees(CancellationToken cancellationToken)
        => Ok(await ActiveEmployeeOptions(cancellationToken));

    [HttpGet("projects/employees")]
    [Authorize(Policy = PermissionCatalog.ProjectsRead)]
    public async Task<ActionResult<IReadOnlyCollection<ManagementEmployeeOption>>> ProjectEmployees(CancellationToken cancellationToken)
        => Ok(await ActiveEmployeeOptions(cancellationToken));

    [HttpGet("tasks/projects")]
    [Authorize(Policy = PermissionCatalog.TasksRead)]
    public async Task<ActionResult<IReadOnlyCollection<ManagementProjectOption>>> TaskProjects(CancellationToken cancellationToken)
        => Ok(await dbContext.Projects.AsNoTracking()
            .Where(x => x.Status != Domain.ProjectStatus.Archived)
            .OrderBy(x => x.NormalizedName)
            .Select(x => new ManagementProjectOption(x.Id, x.Code, x.Name, x.Status.ToString()))
            .ToListAsync(cancellationToken));

    [HttpGet("tasks/projects/{projectId:guid}/members")]
    [Authorize(Policy = PermissionCatalog.TasksRead)]
    public async Task<ActionResult<IReadOnlyCollection<ManagementEmployeeOption>>> TaskProjectMembers(Guid projectId, CancellationToken cancellationToken)
        => Ok(await dbContext.ProjectMembers.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.IsActive && x.Employee.IsActive)
            .OrderBy(x => x.Employee.NormalizedFullName)
            .Select(x => new ManagementEmployeeOption(x.EmployeeId, x.Employee.EmployeeCode, x.Employee.FullName))
            .ToListAsync(cancellationToken));

    private Task<List<ManagementEmployeeOption>> ActiveEmployeeOptions(CancellationToken cancellationToken)
        => dbContext.Employees.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.NormalizedFullName)
            .Select(x => new ManagementEmployeeOption(x.Id, x.EmployeeCode, x.FullName))
            .ToListAsync(cancellationToken);
}
