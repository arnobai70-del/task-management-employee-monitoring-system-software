using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IExternalSurveyService
{
    Task<PagedResponse<ExternalSurveyAssignmentResponse>> GetAssignmentsAsync(
        Guid? employeeId,
        bool? isActive,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ExternalSurveyEmployeeOptionResponse>> GetEmployeeOptionsAsync(CancellationToken cancellationToken);

    Task<OperationResult<ExternalSurveyAssignmentResponse>> CreateAsync(
        UpsertExternalSurveyAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<ExternalSurveyAssignmentResponse>> UpdateAsync(
        Guid id,
        UpsertExternalSurveyAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<IReadOnlyCollection<ExternalSurveyAssignmentResponse>>> GetMineAsync(
        RequestActor actor,
        bool includeInactive,
        CancellationToken cancellationToken);

    Task<OperationResult<ExternalSurveyAssignmentResponse>> RecordOpenAsync(
        Guid id,
        RequestActor actor,
        CancellationToken cancellationToken);
}

public sealed class ExternalSurveyService(AppDbContext dbContext, TimeProvider timeProvider) : IExternalSurveyService
{
    public async Task<PagedResponse<ExternalSurveyAssignmentResponse>> GetAssignmentsAsync(
        Guid? employeeId,
        bool? isActive,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = dbContext.Set<WebsiteAssignment>()
            .AsNoTracking()
            .Where(x => x.AccessLevel == WebsiteAccessLevel.Survey)
            .Include(x => x.Employee)
            .AsQueryable();

        if (employeeId.HasValue)
        {
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        }

        if (isActive.HasValue)
        {
            query = query.Where(x => x.IsActive == isActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToUpperInvariant();
            query = query.Where(x =>
                x.Name.ToUpper().Contains(normalized) ||
                x.Url.ToUpper().Contains(normalized) ||
                (x.Notes != null && x.Notes.ToUpper().Contains(normalized)) ||
                x.Employee.NormalizedEmployeeCode.Contains(normalized) ||
                x.Employee.NormalizedFullName.Contains(normalized));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.ExpiresOn == null)
            .ThenBy(x => x.ExpiresOn)
            .ThenBy(x => x.Employee.NormalizedFullName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<ExternalSurveyAssignmentResponse>(
            rows.Select(ToResponse).ToArray(),
            page,
            pageSize,
            total);
    }

    public async Task<IReadOnlyCollection<ExternalSurveyEmployeeOptionResponse>> GetEmployeeOptionsAsync(CancellationToken cancellationToken)
        => await dbContext.Employees
            .AsNoTracking()
            .Where(x => x.IsActive && x.User.IsActive)
            .OrderBy(x => x.NormalizedFullName)
            .ThenBy(x => x.NormalizedEmployeeCode)
            .Select(x => new ExternalSurveyEmployeeOptionResponse(x.Id, x.EmployeeCode, x.FullName))
            .ToListAsync(cancellationToken);

    public async Task<OperationResult<ExternalSurveyAssignmentResponse>> CreateAsync(
        UpsertExternalSurveyAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(null, request, cancellationToken);
        if (validation.Error is not null)
        {
            return OperationResult<ExternalSurveyAssignmentResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        var now = UtcNow();
        var assignment = new WebsiteAssignment
        {
            EmployeeId = request.EmployeeId,
            Employee = validation.Employee!,
            Name = request.Title.Trim(),
            Url = validation.Url!,
            UsernameReference = null,
            AccessLevel = WebsiteAccessLevel.Survey,
            StartsOn = request.StartsOn,
            ExpiresOn = request.DueDate,
            IsActive = request.IsActive,
            Notes = NormalizeOptional(request.Instructions),
            AssignedByUserId = actor.UserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.Set<WebsiteAssignment>().Add(assignment);
        AddAudit(actor, "survey_link.created", assignment, validation.Host);
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ExternalSurveyAssignmentResponse>.Success(ToResponse(assignment));
    }

    public async Task<OperationResult<ExternalSurveyAssignmentResponse>> UpdateAsync(
        Guid id,
        UpsertExternalSurveyAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var assignment = await dbContext.Set<WebsiteAssignment>()
            .Include(x => x.Employee)
            .SingleOrDefaultAsync(x => x.Id == id && x.AccessLevel == WebsiteAccessLevel.Survey, cancellationToken);
        if (assignment is null)
        {
            return OperationResult<ExternalSurveyAssignmentResponse>.NotFound("survey_link_not_found", "Survey website assignment was not found.");
        }

        var validation = await ValidateAsync(id, request, cancellationToken);
        if (validation.Error is not null)
        {
            return OperationResult<ExternalSurveyAssignmentResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        assignment.EmployeeId = request.EmployeeId;
        assignment.Employee = validation.Employee!;
        assignment.Name = request.Title.Trim();
        assignment.Url = validation.Url!;
        assignment.UsernameReference = null;
        assignment.AccessLevel = WebsiteAccessLevel.Survey;
        assignment.StartsOn = request.StartsOn;
        assignment.ExpiresOn = request.DueDate;
        assignment.IsActive = request.IsActive;
        assignment.Notes = NormalizeOptional(request.Instructions);
        assignment.UpdatedAtUtc = UtcNow();

        AddAudit(actor, "survey_link.updated", assignment, validation.Host);
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ExternalSurveyAssignmentResponse>.Success(ToResponse(assignment));
    }

    public async Task<OperationResult<IReadOnlyCollection<ExternalSurveyAssignmentResponse>>> GetMineAsync(
        RequestActor actor,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var employee = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employee is null)
        {
            return OperationResult<IReadOnlyCollection<ExternalSurveyAssignmentResponse>>.Invalid(
                "employee_profile_required",
                "An active employee profile is required.");
        }

        var query = dbContext.Set<WebsiteAssignment>()
            .AsNoTracking()
            .Where(x => x.EmployeeId == employee.Id && x.AccessLevel == WebsiteAccessLevel.Survey);
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        var rows = await query
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.ExpiresOn == null)
            .ThenBy(x => x.ExpiresOn)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            row.Employee = employee;
        }

        return OperationResult<IReadOnlyCollection<ExternalSurveyAssignmentResponse>>.Success(rows.Select(ToResponse).ToArray());
    }

    public async Task<OperationResult<ExternalSurveyAssignmentResponse>> RecordOpenAsync(
        Guid id,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var employee = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employee is null)
        {
            return OperationResult<ExternalSurveyAssignmentResponse>.Invalid(
                "employee_profile_required",
                "An active employee profile is required.");
        }

        var assignment = await dbContext.Set<WebsiteAssignment>()
            .SingleOrDefaultAsync(x =>
                x.Id == id &&
                x.EmployeeId == employee.Id &&
                x.AccessLevel == WebsiteAccessLevel.Survey,
                cancellationToken);
        if (assignment is null)
        {
            return OperationResult<ExternalSurveyAssignmentResponse>.NotFound("survey_link_not_found", "Survey website assignment was not found.");
        }

        if (!assignment.IsActive)
        {
            return OperationResult<ExternalSurveyAssignmentResponse>.Conflict("survey_link_inactive", "This survey website assignment is inactive.");
        }

        var today = DateOnly.FromDateTime(UtcNow());
        if (assignment.StartsOn.HasValue && assignment.StartsOn.Value > today)
        {
            return OperationResult<ExternalSurveyAssignmentResponse>.Conflict("survey_link_not_started", "This survey assignment is not available yet.");
        }

        if (!Uri.TryCreate(assignment.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return OperationResult<ExternalSurveyAssignmentResponse>.Invalid("survey_link_invalid", "This survey website URL is invalid.");
        }

        assignment.Employee = employee;
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = "survey_link.opened",
            TargetType = "WebsiteAssignment",
            TargetId = assignment.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                assignment.EmployeeId,
                assignment.Name,
                Host = uri.IdnHost.ToLowerInvariant(),
                assignment.ExpiresOn
            }),
            IpAddress = actor.IpAddress,
            UserAgent = Truncate(actor.UserAgent, 512),
            CreatedAtUtc = UtcNow()
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<ExternalSurveyAssignmentResponse>.Success(ToResponse(assignment));
    }

    private async Task<(Employee? Employee, string? Url, string? Host, ApiOperationError? Error)> ValidateAsync(
        Guid? currentId,
        UpsertExternalSurveyAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EmployeeId == Guid.Empty)
        {
            return (null, null, null, new ApiOperationError("employee_required", "Employee is required."));
        }

        var employee = await dbContext.Employees
            .AsNoTracking()
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Id == request.EmployeeId, cancellationToken);
        if (employee is null)
        {
            return (null, null, null, new ApiOperationError("employee_not_found", "Employee was not found."));
        }
        if (request.IsActive && (!employee.IsActive || !employee.User.IsActive))
        {
            return (null, null, null, new ApiOperationError("employee_inactive", "An active survey assignment requires an active employee account."));
        }

        var title = request.Title.Trim();
        if (title.Length is < 2 or > 200)
        {
            return (null, null, null, new ApiOperationError("survey_title_invalid", "Survey title must contain 2 to 200 characters."));
        }

        if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return (null, null, null, new ApiOperationError("survey_url_invalid", "Survey URL must be an absolute HTTP or HTTPS URL without embedded credentials."));
        }

        if (request.StartsOn.HasValue && request.DueDate.HasValue && request.DueDate.Value < request.StartsOn.Value)
        {
            return (null, null, null, new ApiOperationError("survey_date_range_invalid", "Survey due date cannot be before its start date."));
        }

        var url = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        if (request.IsActive && await dbContext.Set<WebsiteAssignment>()
                .AsNoTracking()
                .AnyAsync(x =>
                    x.Id != currentId &&
                    x.EmployeeId == request.EmployeeId &&
                    x.AccessLevel == WebsiteAccessLevel.Survey &&
                    x.Url == url &&
                    x.IsActive,
                    cancellationToken))
        {
            return (null, null, null, new ApiOperationError("survey_link_exists", "This employee already has an active assignment for the same survey URL."));
        }

        return (employee, url, uri.IdnHost.ToLowerInvariant(), null);
    }

    private async Task<Employee?> ResolveEmployeeAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return null;
        }

        return await dbContext.Employees
            .AsNoTracking()
            .Include(x => x.User)
            .SingleOrDefaultAsync(x =>
                x.UserId == actor.UserId.Value &&
                x.IsActive &&
                x.User.IsActive,
                cancellationToken);
    }

    private static ExternalSurveyAssignmentResponse ToResponse(WebsiteAssignment assignment)
        => new(
            assignment.Id,
            assignment.EmployeeId,
            assignment.Employee.EmployeeCode,
            assignment.Employee.FullName,
            assignment.Name,
            assignment.Url,
            assignment.StartsOn,
            assignment.ExpiresOn,
            assignment.IsActive,
            assignment.Notes,
            assignment.CreatedAtUtc,
            assignment.UpdatedAtUtc);

    private void AddAudit(RequestActor actor, string action, WebsiteAssignment assignment, string? host)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = "WebsiteAssignment",
            TargetId = assignment.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                assignment.EmployeeId,
                assignment.Name,
                Host = host,
                assignment.StartsOn,
                DueDate = assignment.ExpiresOn,
                assignment.IsActive
            }),
            IpAddress = actor.IpAddress,
            UserAgent = Truncate(actor.UserAgent, 512),
            CreatedAtUtc = UtcNow()
        });
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Truncate(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
