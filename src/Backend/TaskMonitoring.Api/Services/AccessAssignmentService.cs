using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IAccessAssignmentService
{
    Task<PagedResponse<RdpAssignmentResponse>> GetRdpAssignmentsAsync(Guid? employeeId, bool? isActive, string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<RdpAssignmentResponse>> CreateRdpAssignmentAsync(UpsertRdpAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<RdpAssignmentResponse>> UpdateRdpAssignmentAsync(Guid id, UpsertRdpAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken);

    Task<PagedResponse<IpAssignmentResponse>> GetIpAssignmentsAsync(Guid? employeeId, IpAssignmentStatus? status, string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<IpAssignmentResponse>> CreateIpAssignmentAsync(UpsertIpAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<IpAssignmentResponse>> UpdateIpAssignmentAsync(Guid id, UpsertIpAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken);

    Task<PagedResponse<WebsiteAssignmentResponse>> GetWebsiteAssignmentsAsync(Guid? employeeId, bool? isActive, string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<WebsiteAssignmentResponse>> CreateWebsiteAssignmentAsync(UpsertWebsiteAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<WebsiteAssignmentResponse>> UpdateWebsiteAssignmentAsync(Guid id, UpsertWebsiteAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken);
}

public sealed partial class AccessAssignmentService(AppDbContext dbContext, TimeProvider timeProvider) : IAccessAssignmentService
{
    [GeneratedRegex("^[0-9A-F]{2}(:[0-9A-F]{2}){5}$", RegexOptions.CultureInvariant)]
    private static partial Regex MacRegex();

    public async Task<PagedResponse<RdpAssignmentResponse>> GetRdpAssignmentsAsync(
        Guid? employeeId,
        bool? isActive,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePaging(page, pageSize);
        var query = dbContext.Set<RdpAssignment>().AsNoTracking().Include(x => x.Employee).AsQueryable();

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
            var value = search.Trim().ToUpperInvariant();
            query = query.Where(x =>
                x.Name.ToUpper().Contains(value) ||
                x.Host.ToUpper().Contains(value) ||
                x.Employee.NormalizedEmployeeCode.Contains(value) ||
                x.Employee.NormalizedFullName.Contains(value));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.Employee.NormalizedFullName)
            .ThenBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new RdpAssignmentResponse(
                x.Id,
                x.EmployeeId,
                x.Employee.EmployeeCode,
                x.Employee.FullName,
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

        return new PagedResponse<RdpAssignmentResponse>(items, page, pageSize, total);
    }

    public async Task<OperationResult<RdpAssignmentResponse>> CreateRdpAssignmentAsync(
        UpsertRdpAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateRdpAsync(null, request, cancellationToken);
        if (validation.Error is not null)
        {
            return OperationResult<RdpAssignmentResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        var now = UtcNow();
        var assignment = new RdpAssignment
        {
            EmployeeId = request.EmployeeId,
            Name = request.Name.Trim(),
            Host = NormalizeHost(request.Host),
            Port = request.Port,
            UsernameReference = NormalizeOptional(request.UsernameReference),
            CredentialReference = NormalizeOptional(request.CredentialReference),
            ValidFrom = request.ValidFrom,
            ExpiresOn = request.ExpiresOn,
            IsActive = request.IsActive,
            Notes = NormalizeOptional(request.Notes),
            AssignedByUserId = actor.UserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.Set<RdpAssignment>().Add(assignment);
        AddAudit(actor, "rdp_assignment.created", "RdpAssignment", assignment.Id, new
        {
            assignment.EmployeeId,
            assignment.Name,
            assignment.Host,
            assignment.Port,
            assignment.ValidFrom,
            assignment.ExpiresOn,
            assignment.IsActive
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<RdpAssignmentResponse>.Success(ToRdpResponse(assignment, validation.Employee!));
    }

    public async Task<OperationResult<RdpAssignmentResponse>> UpdateRdpAssignmentAsync(
        Guid id,
        UpsertRdpAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var assignment = await dbContext.Set<RdpAssignment>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (assignment is null)
        {
            return OperationResult<RdpAssignmentResponse>.NotFound("rdp_assignment_not_found", "RDP assignment was not found.");
        }

        var validation = await ValidateRdpAsync(id, request, cancellationToken);
        if (validation.Error is not null)
        {
            return OperationResult<RdpAssignmentResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        assignment.EmployeeId = request.EmployeeId;
        assignment.Name = request.Name.Trim();
        assignment.Host = NormalizeHost(request.Host);
        assignment.Port = request.Port;
        assignment.UsernameReference = NormalizeOptional(request.UsernameReference);
        assignment.CredentialReference = NormalizeOptional(request.CredentialReference);
        assignment.ValidFrom = request.ValidFrom;
        assignment.ExpiresOn = request.ExpiresOn;
        assignment.IsActive = request.IsActive;
        assignment.Notes = NormalizeOptional(request.Notes);
        assignment.UpdatedAtUtc = UtcNow();

        AddAudit(actor, "rdp_assignment.updated", "RdpAssignment", assignment.Id, new
        {
            assignment.EmployeeId,
            assignment.Name,
            assignment.Host,
            assignment.Port,
            assignment.ValidFrom,
            assignment.ExpiresOn,
            assignment.IsActive
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<RdpAssignmentResponse>.Success(ToRdpResponse(assignment, validation.Employee!));
    }

    public async Task<PagedResponse<IpAssignmentResponse>> GetIpAssignmentsAsync(
        Guid? employeeId,
        IpAssignmentStatus? status,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePaging(page, pageSize);
        var query = dbContext.Set<IpAssignment>().AsNoTracking().Include(x => x.Employee).AsQueryable();

        if (employeeId.HasValue)
        {
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim().ToUpperInvariant();
            query = query.Where(x =>
                x.IpAddress.ToUpper().Contains(value) ||
                x.DeviceName.ToUpper().Contains(value) ||
                (x.MacAddress != null && x.MacAddress.ToUpper().Contains(value)) ||
                x.Employee.NormalizedEmployeeCode.Contains(value) ||
                x.Employee.NormalizedFullName.Contains(value));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.Status == IpAssignmentStatus.Released)
            .ThenBy(x => x.IpAddress)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new IpAssignmentResponse(
                x.Id,
                x.EmployeeId,
                x.Employee.EmployeeCode,
                x.Employee.FullName,
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

        return new PagedResponse<IpAssignmentResponse>(items, page, pageSize, total);
    }

    public async Task<OperationResult<IpAssignmentResponse>> CreateIpAssignmentAsync(
        UpsertIpAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateIpAsync(null, request, cancellationToken);
        if (validation.Error is not null)
        {
            return OperationResult<IpAssignmentResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        var now = UtcNow();
        var assignment = new IpAssignment
        {
            EmployeeId = request.EmployeeId,
            IpAddress = validation.IpAddress!,
            DeviceName = request.DeviceName.Trim(),
            MacAddress = validation.MacAddress,
            Status = request.Status,
            AssignedOn = request.AssignedOn ?? (request.Status == IpAssignmentStatus.Released ? null : DateOnly.FromDateTime(now)),
            ReleasedOn = request.Status == IpAssignmentStatus.Released ? request.ReleasedOn ?? DateOnly.FromDateTime(now) : null,
            Notes = NormalizeOptional(request.Notes),
            AssignedByUserId = actor.UserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.Set<IpAssignment>().Add(assignment);
        AddAudit(actor, "ip_assignment.created", "IpAssignment", assignment.Id, new
        {
            assignment.EmployeeId,
            assignment.IpAddress,
            assignment.DeviceName,
            assignment.MacAddress,
            assignment.Status,
            assignment.AssignedOn,
            assignment.ReleasedOn
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<IpAssignmentResponse>.Success(ToIpResponse(assignment, validation.Employee!));
    }

    public async Task<OperationResult<IpAssignmentResponse>> UpdateIpAssignmentAsync(
        Guid id,
        UpsertIpAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var assignment = await dbContext.Set<IpAssignment>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (assignment is null)
        {
            return OperationResult<IpAssignmentResponse>.NotFound("ip_assignment_not_found", "IP assignment was not found.");
        }

        var validation = await ValidateIpAsync(id, request, cancellationToken);
        if (validation.Error is not null)
        {
            return OperationResult<IpAssignmentResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        var now = UtcNow();
        assignment.EmployeeId = request.EmployeeId;
        assignment.IpAddress = validation.IpAddress!;
        assignment.DeviceName = request.DeviceName.Trim();
        assignment.MacAddress = validation.MacAddress;
        assignment.Status = request.Status;
        assignment.AssignedOn = request.AssignedOn ?? assignment.AssignedOn ?? (request.Status == IpAssignmentStatus.Released ? null : DateOnly.FromDateTime(now));
        assignment.ReleasedOn = request.Status == IpAssignmentStatus.Released ? request.ReleasedOn ?? assignment.ReleasedOn ?? DateOnly.FromDateTime(now) : null;
        assignment.Notes = NormalizeOptional(request.Notes);
        assignment.UpdatedAtUtc = now;

        AddAudit(actor, "ip_assignment.updated", "IpAssignment", assignment.Id, new
        {
            assignment.EmployeeId,
            assignment.IpAddress,
            assignment.DeviceName,
            assignment.MacAddress,
            assignment.Status,
            assignment.AssignedOn,
            assignment.ReleasedOn
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<IpAssignmentResponse>.Success(ToIpResponse(assignment, validation.Employee!));
    }

    public async Task<PagedResponse<WebsiteAssignmentResponse>> GetWebsiteAssignmentsAsync(
        Guid? employeeId,
        bool? isActive,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePaging(page, pageSize);
        var query = dbContext.Set<WebsiteAssignment>().AsNoTracking().Include(x => x.Employee).AsQueryable();

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
            var value = search.Trim().ToUpperInvariant();
            query = query.Where(x =>
                x.Name.ToUpper().Contains(value) ||
                x.Url.ToUpper().Contains(value) ||
                x.Employee.NormalizedEmployeeCode.Contains(value) ||
                x.Employee.NormalizedFullName.Contains(value));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.Employee.NormalizedFullName)
            .ThenBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new WebsiteAssignmentResponse(
                x.Id,
                x.EmployeeId,
                x.Employee.EmployeeCode,
                x.Employee.FullName,
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

        return new PagedResponse<WebsiteAssignmentResponse>(items, page, pageSize, total);
    }

    public async Task<OperationResult<WebsiteAssignmentResponse>> CreateWebsiteAssignmentAsync(
        UpsertWebsiteAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateWebsiteAsync(null, request, cancellationToken);
        if (validation.Error is not null)
        {
            return OperationResult<WebsiteAssignmentResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        var now = UtcNow();
        var assignment = new WebsiteAssignment
        {
            EmployeeId = request.EmployeeId,
            Name = request.Name.Trim(),
            Url = validation.Url!,
            UsernameReference = NormalizeOptional(request.UsernameReference),
            AccessLevel = request.AccessLevel,
            StartsOn = request.StartsOn,
            ExpiresOn = request.ExpiresOn,
            IsActive = request.IsActive,
            Notes = NormalizeOptional(request.Notes),
            AssignedByUserId = actor.UserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.Set<WebsiteAssignment>().Add(assignment);
        AddAudit(actor, "website_assignment.created", "WebsiteAssignment", assignment.Id, new
        {
            assignment.EmployeeId,
            assignment.Name,
            assignment.Url,
            assignment.AccessLevel,
            assignment.StartsOn,
            assignment.ExpiresOn,
            assignment.IsActive
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<WebsiteAssignmentResponse>.Success(ToWebsiteResponse(assignment, validation.Employee!));
    }

    public async Task<OperationResult<WebsiteAssignmentResponse>> UpdateWebsiteAssignmentAsync(
        Guid id,
        UpsertWebsiteAssignmentRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var assignment = await dbContext.Set<WebsiteAssignment>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (assignment is null)
        {
            return OperationResult<WebsiteAssignmentResponse>.NotFound("website_assignment_not_found", "Website assignment was not found.");
        }

        var validation = await ValidateWebsiteAsync(id, request, cancellationToken);
        if (validation.Error is not null)
        {
            return OperationResult<WebsiteAssignmentResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        assignment.EmployeeId = request.EmployeeId;
        assignment.Name = request.Name.Trim();
        assignment.Url = validation.Url!;
        assignment.UsernameReference = NormalizeOptional(request.UsernameReference);
        assignment.AccessLevel = request.AccessLevel;
        assignment.StartsOn = request.StartsOn;
        assignment.ExpiresOn = request.ExpiresOn;
        assignment.IsActive = request.IsActive;
        assignment.Notes = NormalizeOptional(request.Notes);
        assignment.UpdatedAtUtc = UtcNow();

        AddAudit(actor, "website_assignment.updated", "WebsiteAssignment", assignment.Id, new
        {
            assignment.EmployeeId,
            assignment.Name,
            assignment.Url,
            assignment.AccessLevel,
            assignment.StartsOn,
            assignment.ExpiresOn,
            assignment.IsActive
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<WebsiteAssignmentResponse>.Success(ToWebsiteResponse(assignment, validation.Employee!));
    }

    private async Task<(Employee? Employee, ApiOperationError? Error)> ValidateEmployeeAsync(Guid employeeId, bool requireActive, CancellationToken cancellationToken)
    {
        if (employeeId == Guid.Empty)
        {
            return (null, new ApiOperationError("employee_required", "Employee is required."));
        }

        var employee = await dbContext.Employees.AsNoTracking().SingleOrDefaultAsync(x => x.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return (null, new ApiOperationError("employee_not_found", "Employee was not found."));
        }

        if (requireActive && !employee.IsActive)
        {
            return (null, new ApiOperationError("employee_inactive", "Active assignments cannot be given to an inactive employee."));
        }

        return (employee, null);
    }

    private async Task<(Employee? Employee, ApiOperationError? Error)> ValidateRdpAsync(Guid? currentId, UpsertRdpAssignmentRequest request, CancellationToken cancellationToken)
    {
        var employeeResult = await ValidateEmployeeAsync(request.EmployeeId, request.IsActive, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return employeeResult;
        }

        if (request.ValidFrom.HasValue && request.ExpiresOn.HasValue && request.ExpiresOn < request.ValidFrom)
        {
            return (null, new ApiOperationError("invalid_date_range", "RDP expiry date cannot be before its valid-from date."));
        }

        var host = NormalizeHost(request.Host);
        if (request.IsActive && await dbContext.Set<RdpAssignment>().AsNoTracking().AnyAsync(
                x => x.Id != currentId && x.EmployeeId == request.EmployeeId && x.Host == host && x.Port == request.Port && x.IsActive,
                cancellationToken))
        {
            return (null, new ApiOperationError("rdp_assignment_exists", "This employee already has an active RDP assignment for the same host and port."));
        }

        return employeeResult;
    }

    private async Task<(Employee? Employee, string? IpAddress, string? MacAddress, ApiOperationError? Error)> ValidateIpAsync(
        Guid? currentId,
        UpsertIpAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ValidateEmployeeAsync(request.EmployeeId, request.Status != IpAssignmentStatus.Released, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return (null, null, null, employeeResult.Error);
        }

        if (!IPAddress.TryParse(request.IpAddress.Trim(), out var parsed))
        {
            return (null, null, null, new ApiOperationError("invalid_ip_address", "IP address is not valid."));
        }

        var ipAddress = parsed.ToString();
        var macAddress = NormalizeMac(request.MacAddress);
        if (macAddress is not null && !MacRegex().IsMatch(macAddress))
        {
            return (null, null, null, new ApiOperationError("invalid_mac_address", "MAC address must contain six hexadecimal byte pairs."));
        }

        if (request.AssignedOn.HasValue && request.ReleasedOn.HasValue && request.ReleasedOn < request.AssignedOn)
        {
            return (null, null, null, new ApiOperationError("invalid_date_range", "Release date cannot be before the assignment date."));
        }

        if (request.Status != IpAssignmentStatus.Released && await dbContext.Set<IpAssignment>().AsNoTracking().AnyAsync(
                x => x.Id != currentId && x.IpAddress == ipAddress && x.Status != IpAssignmentStatus.Released,
                cancellationToken))
        {
            return (null, null, null, new ApiOperationError("ip_address_in_use", "This IP address is already active or reserved."));
        }

        return (employeeResult.Employee, ipAddress, macAddress, null);
    }

    private async Task<(Employee? Employee, string? Url, ApiOperationError? Error)> ValidateWebsiteAsync(
        Guid? currentId,
        UpsertWebsiteAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ValidateEmployeeAsync(request.EmployeeId, request.IsActive, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return (null, null, employeeResult.Error);
        }

        if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (null, null, new ApiOperationError("invalid_website_url", "Website URL must be an absolute HTTP or HTTPS URL."));
        }

        if (request.StartsOn.HasValue && request.ExpiresOn.HasValue && request.ExpiresOn < request.StartsOn)
        {
            return (null, null, new ApiOperationError("invalid_date_range", "Website access expiry cannot be before its start date."));
        }

        var url = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        if (request.IsActive && await dbContext.Set<WebsiteAssignment>().AsNoTracking().AnyAsync(
                x => x.Id != currentId && x.EmployeeId == request.EmployeeId && x.Url == url && x.IsActive,
                cancellationToken))
        {
            return (null, null, new ApiOperationError("website_assignment_exists", "This employee already has active access to the same website URL."));
        }

        return (employeeResult.Employee, url, null);
    }

    private static RdpAssignmentResponse ToRdpResponse(RdpAssignment assignment, Employee employee) => new(
        assignment.Id,
        assignment.EmployeeId,
        employee.EmployeeCode,
        employee.FullName,
        assignment.Name,
        assignment.Host,
        assignment.Port,
        assignment.UsernameReference,
        assignment.CredentialReference,
        assignment.ValidFrom,
        assignment.ExpiresOn,
        assignment.IsActive,
        assignment.Notes,
        assignment.CreatedAtUtc,
        assignment.UpdatedAtUtc);

    private static IpAssignmentResponse ToIpResponse(IpAssignment assignment, Employee employee) => new(
        assignment.Id,
        assignment.EmployeeId,
        employee.EmployeeCode,
        employee.FullName,
        assignment.IpAddress,
        assignment.DeviceName,
        assignment.MacAddress,
        assignment.Status,
        assignment.AssignedOn,
        assignment.ReleasedOn,
        assignment.Notes,
        assignment.CreatedAtUtc,
        assignment.UpdatedAtUtc);

    private static WebsiteAssignmentResponse ToWebsiteResponse(WebsiteAssignment assignment, Employee employee) => new(
        assignment.Id,
        assignment.EmployeeId,
        employee.EmployeeCode,
        employee.FullName,
        assignment.Name,
        assignment.Url,
        assignment.UsernameReference,
        assignment.AccessLevel,
        assignment.StartsOn,
        assignment.ExpiresOn,
        assignment.IsActive,
        assignment.Notes,
        assignment.CreatedAtUtc,
        assignment.UpdatedAtUtc);

    private void AddAudit(RequestActor actor, string action, string targetType, Guid targetId, object metadata)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId.ToString(),
            MetadataJson = JsonSerializer.Serialize(metadata),
            IpAddress = actor.IpAddress,
            UserAgent = Truncate(actor.UserAgent, 512),
            CreatedAtUtc = UtcNow()
        });
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize) => (Math.Clamp(page, 1, 1_000_000), Math.Clamp(pageSize, 1, 100));
    private static string NormalizeHost(string value) => value.Trim().ToLowerInvariant();
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var raw = value.Trim().Replace('-', ':').ToUpperInvariant();
        if (!raw.Contains(':', StringComparison.Ordinal) && raw.Length == 12)
        {
            raw = string.Join(':', Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2)));
        }

        return raw;
    }

    private static string? Truncate(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
