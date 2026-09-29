using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IEmployeeWorkspaceService
{
    Task<OperationResult<PagedResponse<ProjectTaskResponse>>> GetMyTasksAsync(
        RequestActor actor,
        bool includeClosed,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OperationResult<EmployeeAccessWorkspaceResponse>> GetMyAccessAsync(
        RequestActor actor,
        bool includeInactive,
        CancellationToken cancellationToken);
}

public sealed class EmployeeWorkspaceService(AppDbContext dbContext) : IEmployeeWorkspaceService
{
    public async Task<OperationResult<PagedResponse<ProjectTaskResponse>>> GetMyTasksAsync(
        RequestActor actor,
        bool includeClosed,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Status != OperationStatus.Success || employeeResult.Value is null)
        {
            return ForwardError<PagedResponse<ProjectTaskResponse>, Employee>(employeeResult);
        }

        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var employee = employeeResult.Value;

        var query = dbContext.ProjectTasks
            .AsNoTracking()
            .Where(x => x.AssigneeEmployeeId == employee.Id);

        if (!includeClosed)
        {
            query = query.Where(x => x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.DueDate == null)
            .ThenBy(x => x.DueDate)
            .ThenByDescending(x => x.Priority)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ProjectTaskResponse(
                x.Id,
                x.ProjectId,
                x.Project.Code,
                x.Project.Name,
                x.Title,
                x.Description,
                x.Status,
                x.Priority,
                x.AssigneeEmployeeId,
                x.AssigneeEmployee != null ? x.AssigneeEmployee.FullName : null,
                x.DueDate,
                x.CompletedAtUtc,
                x.Comments.Count,
                x.CreatedAtUtc,
                x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return OperationResult<PagedResponse<ProjectTaskResponse>>.Success(
            new PagedResponse<ProjectTaskResponse>(items, page, pageSize, total));
    }

    public async Task<OperationResult<EmployeeAccessWorkspaceResponse>> GetMyAccessAsync(
        RequestActor actor,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Status != OperationStatus.Success || employeeResult.Value is null)
        {
            return ForwardError<EmployeeAccessWorkspaceResponse, Employee>(employeeResult);
        }

        var employee = employeeResult.Value;

        var rdpQuery = dbContext.Set<RdpAssignment>()
            .AsNoTracking()
            .Where(x => x.EmployeeId == employee.Id);
        if (!includeInactive)
        {
            rdpQuery = rdpQuery.Where(x => x.IsActive);
        }

        var ipQuery = dbContext.Set<IpAssignment>()
            .AsNoTracking()
            .Where(x => x.EmployeeId == employee.Id);
        if (!includeInactive)
        {
            ipQuery = ipQuery.Where(x => x.Status != IpAssignmentStatus.Released);
        }

        var websiteQuery = dbContext.Set<WebsiteAssignment>()
            .AsNoTracking()
            .Where(x => x.EmployeeId == employee.Id);
        if (!includeInactive)
        {
            websiteQuery = websiteQuery.Where(x => x.IsActive);
        }

        var rdpAssignments = await rdpQuery
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.Name)
            .Select(x => new RdpAssignmentResponse(
                x.Id,
                employee.Id,
                employee.EmployeeCode,
                employee.FullName,
                x.Name,
                x.Host,
                x.Port,
                x.UsernameReference,
                x.CredentialReference,
                x.ValidFrom,
                x.ExpiresOn,
                x.IsActive,
                x.Notes,
                x.CreatedAtUtc,
                x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        var ipAssignments = await ipQuery
            .OrderBy(x => x.Status == IpAssignmentStatus.Released)
            .ThenBy(x => x.IpAddress)
            .Select(x => new IpAssignmentResponse(
                x.Id,
                employee.Id,
                employee.EmployeeCode,
                employee.FullName,
                x.IpAddress,
                x.DeviceName,
                x.MacAddress,
                x.Status,
                x.AssignedOn,
                x.ReleasedOn,
                x.Notes,
                x.CreatedAtUtc,
                x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        var websiteAssignments = await websiteQuery
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.Name)
            .Select(x => new WebsiteAssignmentResponse(
                x.Id,
                employee.Id,
                employee.EmployeeCode,
                employee.FullName,
                x.Name,
                x.Url,
                x.UsernameReference,
                x.AccessLevel,
                x.StartsOn,
                x.ExpiresOn,
                x.IsActive,
                x.Notes,
                x.CreatedAtUtc,
                x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return OperationResult<EmployeeAccessWorkspaceResponse>.Success(
            new EmployeeAccessWorkspaceResponse(rdpAssignments, ipAssignments, websiteAssignments));
    }

    private async Task<OperationResult<Employee>> ResolveEmployeeAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return OperationResult<Employee>.Invalid("authenticated_user_required", "An authenticated user is required.");
        }

        var employee = await dbContext.Employees
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == actor.UserId.Value, cancellationToken);

        if (employee is null)
        {
            return OperationResult<Employee>.NotFound("employee_profile_not_found", "No employee profile is linked to this account.");
        }

        if (!employee.IsActive)
        {
            return OperationResult<Employee>.Invalid("employee_inactive", "The employee profile is inactive.");
        }

        return OperationResult<Employee>.Success(employee);
    }

    private static OperationResult<TTarget> ForwardError<TTarget, TSource>(OperationResult<TSource> source)
        => source.Status switch
        {
            OperationStatus.NotFound => OperationResult<TTarget>.NotFound(source.ErrorCode ?? "not_found", source.Message ?? "The requested record was not found."),
            OperationStatus.Conflict => OperationResult<TTarget>.Conflict(source.ErrorCode ?? "conflict", source.Message ?? "The request conflicts with the current state."),
            _ => OperationResult<TTarget>.Invalid(source.ErrorCode ?? "request_invalid", source.Message ?? "The request is invalid.")
        };
}
