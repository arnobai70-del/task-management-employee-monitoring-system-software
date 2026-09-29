using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IMonitoringTelemetryService
{
    Task<MonitoringPolicyResponse> GetPolicyAsync(CancellationToken cancellationToken);
    Task<OperationResult<MonitoringPolicyResponse>> UpdatePolicyAsync(UpdateMonitoringPolicyRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ApprovedApplicationResponse>> GetApplicationsAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<OperationResult<ApprovedApplicationResponse>> CreateApplicationAsync(UpsertApprovedApplicationRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ApprovedApplicationResponse>> UpdateApplicationAsync(Guid id, UpsertApprovedApplicationRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ApprovedBusinessDomainResponse>> GetDomainsAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<OperationResult<ApprovedBusinessDomainResponse>> CreateDomainAsync(UpsertApprovedBusinessDomainRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ApprovedBusinessDomainResponse>> UpdateDomainAsync(Guid id, UpsertApprovedBusinessDomainRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<PagedResponse<MonitoringActivityResponse>>> GetActivityAsync(Guid? employeeId, MonitoringActivityKind? kind, DateTime? fromUtc, DateTime? toUtc, string? search, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<EmployeeMonitoringPolicyResponse>> GetMyPolicyAsync(RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<MonitoringIngestResponse>> RecordApplicationAsync(RequestActor actor, RecordApplicationActivityRequest request, CancellationToken cancellationToken);
    Task<OperationResult<MonitoringIngestResponse>> RecordDomainAsync(RequestActor actor, RecordBusinessDomainActivityRequest request, CancellationToken cancellationToken);
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}

public sealed class MonitoringTelemetryService(AppDbContext dbContext, TimeProvider timeProvider) : IMonitoringTelemetryService
{
    public async Task<MonitoringPolicyResponse> GetPolicyAsync(CancellationToken cancellationToken)
        => ToPolicyResponse(await dbContext.MonitoringPolicies.AsNoTracking().OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken));

    public async Task<OperationResult<MonitoringPolicyResponse>> UpdatePolicyAsync(
        UpdateMonitoringPolicyRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DisclosureText))
        {
            return OperationResult<MonitoringPolicyResponse>.Invalid("disclosure_required", "Employee monitoring disclosure text is required.");
        }

        var now = UtcNow();
        var policy = await dbContext.MonitoringPolicies.OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (policy is null)
        {
            policy = new MonitoringPolicy
            {
                IsEnabled = request.IsEnabled,
                SampleIntervalSeconds = request.SampleIntervalSeconds,
                RetentionDays = request.RetentionDays,
                DisclosureText = request.DisclosureText.Trim(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            dbContext.MonitoringPolicies.Add(policy);
        }
        else
        {
            policy.IsEnabled = request.IsEnabled;
            policy.SampleIntervalSeconds = request.SampleIntervalSeconds;
            policy.RetentionDays = request.RetentionDays;
            policy.DisclosureText = request.DisclosureText.Trim();
            policy.UpdatedAtUtc = now;
        }

        AddAudit(actor, "monitoring.policy.updated", "MonitoringPolicy", policy.Id, new
        {
            policy.IsEnabled,
            policy.SampleIntervalSeconds,
            policy.RetentionDays
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await PurgeExpiredAsync(cancellationToken);
        return OperationResult<MonitoringPolicyResponse>.Success(ToPolicyResponse(policy));
    }

    public async Task<IReadOnlyCollection<ApprovedApplicationResponse>> GetApplicationsAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var query = dbContext.ApprovedApplications.AsNoTracking().AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.DisplayName)
            .Select(x => new ApprovedApplicationResponse(x.Id, x.ProcessName, x.DisplayName, x.CaptureWindowTitle, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult<ApprovedApplicationResponse>> CreateApplicationAsync(
        UpsertApprovedApplicationRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var normalizedResult = NormalizeProcessName(request.ProcessName);
        if (normalizedResult.Error is not null)
        {
            return OperationResult<ApprovedApplicationResponse>.Invalid(normalizedResult.Error.Code, normalizedResult.Error.Message);
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return OperationResult<ApprovedApplicationResponse>.Invalid("application_name_required", "Application display name is required.");
        }

        var normalized = normalizedResult.Value!;
        if (await dbContext.ApprovedApplications.AsNoTracking().AnyAsync(x => x.NormalizedProcessName == normalized, cancellationToken))
        {
            return OperationResult<ApprovedApplicationResponse>.Conflict("application_rule_exists", "An application rule already exists for this process name.");
        }

        var now = UtcNow();
        var entity = new ApprovedApplication
        {
            ProcessName = normalized,
            NormalizedProcessName = normalized,
            DisplayName = request.DisplayName.Trim(),
            CaptureWindowTitle = request.CaptureWindowTitle,
            IsActive = request.IsActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        dbContext.ApprovedApplications.Add(entity);
        AddAudit(actor, "monitoring.application.created", "ApprovedApplication", entity.Id, new
        {
            entity.ProcessName,
            entity.DisplayName,
            entity.CaptureWindowTitle,
            entity.IsActive
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ApprovedApplicationResponse>.Success(ToApplicationResponse(entity));
    }

    public async Task<OperationResult<ApprovedApplicationResponse>> UpdateApplicationAsync(
        Guid id,
        UpsertApprovedApplicationRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ApprovedApplications.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return OperationResult<ApprovedApplicationResponse>.NotFound("application_rule_not_found", "Application rule was not found.");
        }

        var normalizedResult = NormalizeProcessName(request.ProcessName);
        if (normalizedResult.Error is not null)
        {
            return OperationResult<ApprovedApplicationResponse>.Invalid(normalizedResult.Error.Code, normalizedResult.Error.Message);
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return OperationResult<ApprovedApplicationResponse>.Invalid("application_name_required", "Application display name is required.");
        }

        var normalized = normalizedResult.Value!;
        if (await dbContext.ApprovedApplications.AsNoTracking().AnyAsync(x => x.Id != id && x.NormalizedProcessName == normalized, cancellationToken))
        {
            return OperationResult<ApprovedApplicationResponse>.Conflict("application_rule_exists", "An application rule already exists for this process name.");
        }

        entity.ProcessName = normalized;
        entity.NormalizedProcessName = normalized;
        entity.DisplayName = request.DisplayName.Trim();
        entity.CaptureWindowTitle = request.CaptureWindowTitle;
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = UtcNow();
        AddAudit(actor, "monitoring.application.updated", "ApprovedApplication", entity.Id, new
        {
            entity.ProcessName,
            entity.DisplayName,
            entity.CaptureWindowTitle,
            entity.IsActive
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ApprovedApplicationResponse>.Success(ToApplicationResponse(entity));
    }

    public async Task<IReadOnlyCollection<ApprovedBusinessDomainResponse>> GetDomainsAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var query = dbContext.ApprovedBusinessDomains.AsNoTracking().AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.Domain)
            .Select(x => new ApprovedBusinessDomainResponse(x.Id, x.Domain, x.DisplayName, x.IncludeSubdomains, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationResult<ApprovedBusinessDomainResponse>> CreateDomainAsync(
        UpsertApprovedBusinessDomainRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var normalizedResult = NormalizeDomain(request.Domain);
        if (normalizedResult.Error is not null)
        {
            return OperationResult<ApprovedBusinessDomainResponse>.Invalid(normalizedResult.Error.Code, normalizedResult.Error.Message);
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return OperationResult<ApprovedBusinessDomainResponse>.Invalid("domain_name_required", "Business domain display name is required.");
        }

        var normalized = normalizedResult.Value!;
        if (await dbContext.ApprovedBusinessDomains.AsNoTracking().AnyAsync(x => x.NormalizedDomain == normalized, cancellationToken))
        {
            return OperationResult<ApprovedBusinessDomainResponse>.Conflict("domain_rule_exists", "A business-domain rule already exists for this hostname.");
        }

        var now = UtcNow();
        var entity = new ApprovedBusinessDomain
        {
            Domain = normalized,
            NormalizedDomain = normalized,
            DisplayName = request.DisplayName.Trim(),
            IncludeSubdomains = request.IncludeSubdomains,
            IsActive = request.IsActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        dbContext.ApprovedBusinessDomains.Add(entity);
        AddAudit(actor, "monitoring.domain.created", "ApprovedBusinessDomain", entity.Id, new
        {
            entity.Domain,
            entity.DisplayName,
            entity.IncludeSubdomains,
            entity.IsActive
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ApprovedBusinessDomainResponse>.Success(ToDomainResponse(entity));
    }

    public async Task<OperationResult<ApprovedBusinessDomainResponse>> UpdateDomainAsync(
        Guid id,
        UpsertApprovedBusinessDomainRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ApprovedBusinessDomains.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return OperationResult<ApprovedBusinessDomainResponse>.NotFound("domain_rule_not_found", "Business-domain rule was not found.");
        }

        var normalizedResult = NormalizeDomain(request.Domain);
        if (normalizedResult.Error is not null)
        {
            return OperationResult<ApprovedBusinessDomainResponse>.Invalid(normalizedResult.Error.Code, normalizedResult.Error.Message);
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return OperationResult<ApprovedBusinessDomainResponse>.Invalid("domain_name_required", "Business domain display name is required.");
        }

        var normalized = normalizedResult.Value!;
        if (await dbContext.ApprovedBusinessDomains.AsNoTracking().AnyAsync(x => x.Id != id && x.NormalizedDomain == normalized, cancellationToken))
        {
            return OperationResult<ApprovedBusinessDomainResponse>.Conflict("domain_rule_exists", "A business-domain rule already exists for this hostname.");
        }

        entity.Domain = normalized;
        entity.NormalizedDomain = normalized;
        entity.DisplayName = request.DisplayName.Trim();
        entity.IncludeSubdomains = request.IncludeSubdomains;
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = UtcNow();
        AddAudit(actor, "monitoring.domain.updated", "ApprovedBusinessDomain", entity.Id, new
        {
            entity.Domain,
            entity.DisplayName,
            entity.IncludeSubdomains,
            entity.IsActive
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<ApprovedBusinessDomainResponse>.Success(ToDomainResponse(entity));
    }

    public async Task<OperationResult<PagedResponse<MonitoringActivityResponse>>> GetActivityAsync(
        Guid? employeeId,
        MonitoringActivityKind? kind,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var policy = await dbContext.MonitoringPolicies.AsNoTracking().OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var retentionDays = policy?.RetentionDays ?? MonitoringDefaults.RetentionDays;
        var retentionCutoff = UtcNow().AddDays(-retentionDays);
        var from = NormalizeUtc(fromUtc) ?? retentionCutoff;
        if (from < retentionCutoff)
        {
            from = retentionCutoff;
        }
        var to = NormalizeUtc(toUtc) ?? UtcNow();
        if (to < from)
        {
            return OperationResult<PagedResponse<MonitoringActivityResponse>>.Invalid("invalid_range", "The monitoring end time cannot be before the start time.");
        }
        if (to - from > TimeSpan.FromDays(31))
        {
            return OperationResult<PagedResponse<MonitoringActivityResponse>>.Invalid("range_too_large", "Monitoring activity queries are limited to 31 days per request.");
        }

        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.MonitoringActivitySegments
            .AsNoTracking()
            .Include(x => x.Employee)
            .ThenInclude(x => x.Department)
            .Where(x => x.LastObservedAtUtc >= from && x.StartedAtUtc <= to);
        if (employeeId.HasValue)
        {
            query = query.Where(x => x.EmployeeId == employeeId.Value);
        }
        if (kind.HasValue)
        {
            query = query.Where(x => x.Kind == kind.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim().ToUpperInvariant();
            query = query.Where(x =>
                x.Employee.NormalizedFullName.Contains(value) ||
                x.Employee.NormalizedEmployeeCode.Contains(value) ||
                (x.ProcessName != null && x.ProcessName.ToUpper().Contains(value)) ||
                (x.ApplicationName != null && x.ApplicationName.ToUpper().Contains(value)) ||
                (x.Domain != null && x.Domain.ToUpper().Contains(value)) ||
                (x.WindowTitle != null && x.WindowTitle.ToUpper().Contains(value)));
        }

        var total = await query.CountAsync(cancellationToken);
        var entities = await query
            .OrderByDescending(x => x.LastObservedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = entities.Select(ToActivityResponse).ToArray();
        return OperationResult<PagedResponse<MonitoringActivityResponse>>.Success(new PagedResponse<MonitoringActivityResponse>(items, page, pageSize, total));
    }

    public async Task<OperationResult<EmployeeMonitoringPolicyResponse>> GetMyPolicyAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return OperationResult<EmployeeMonitoringPolicyResponse>.Invalid(employeeResult.Error.Code, employeeResult.Error.Message);
        }

        var policy = ToPolicyResponse(await dbContext.MonitoringPolicies.AsNoTracking().OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken));
        var applications = await GetApplicationsAsync(false, cancellationToken);
        var configuredDomains = (await GetDomainsAsync(false, cancellationToken)).ToList();
        var assignedDomains = await GetAssignedDomainsAsync(employeeResult.Employee!.Id, cancellationToken);
        var domainMap = configuredDomains.ToDictionary(x => x.Domain, StringComparer.OrdinalIgnoreCase);
        foreach (var assigned in assignedDomains)
        {
            domainMap.TryAdd(assigned.Domain, assigned);
        }

        return OperationResult<EmployeeMonitoringPolicyResponse>.Success(new EmployeeMonitoringPolicyResponse(
            policy.IsEnabled,
            policy.SampleIntervalSeconds,
            policy.RetentionDays,
            policy.DisclosureText,
            applications,
            domainMap.Values.OrderBy(x => x.Domain).ToArray()));
    }

    public async Task<OperationResult<MonitoringIngestResponse>> RecordApplicationAsync(
        RequestActor actor,
        RecordApplicationActivityRequest request,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return OperationResult<MonitoringIngestResponse>.Invalid(employeeResult.Error.Code, employeeResult.Error.Message);
        }

        var policy = await GetEffectivePolicyAsync(cancellationToken);
        if (!policy.IsEnabled)
        {
            return OperationResult<MonitoringIngestResponse>.Success(new MonitoringIngestResponse(false, "monitoring_disabled"));
        }

        var normalizedResult = NormalizeProcessName(request.ProcessName);
        if (normalizedResult.Error is not null)
        {
            return OperationResult<MonitoringIngestResponse>.Success(new MonitoringIngestResponse(false, "application_not_approved"));
        }
        var processName = normalizedResult.Value!;
        var rule = await dbContext.ApprovedApplications.AsNoTracking().SingleOrDefaultAsync(
            x => x.NormalizedProcessName == processName && x.IsActive,
            cancellationToken);
        if (rule is null)
        {
            return OperationResult<MonitoringIngestResponse>.Success(new MonitoringIngestResponse(false, "application_not_approved"));
        }

        var title = rule.CaptureWindowTitle && !string.IsNullOrWhiteSpace(request.WindowTitle)
            ? Truncate(request.WindowTitle.Trim(), 300)
            : null;
        await UpsertSegmentAsync(employeeResult.Employee!.Id, MonitoringActivityKind.Application, processName, rule.DisplayName, title, null, policy.SampleIntervalSeconds, cancellationToken);
        return OperationResult<MonitoringIngestResponse>.Success(new MonitoringIngestResponse(true, null));
    }

    public async Task<OperationResult<MonitoringIngestResponse>> RecordDomainAsync(
        RequestActor actor,
        RecordBusinessDomainActivityRequest request,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return OperationResult<MonitoringIngestResponse>.Invalid(employeeResult.Error.Code, employeeResult.Error.Message);
        }

        var policy = await GetEffectivePolicyAsync(cancellationToken);
        if (!policy.IsEnabled)
        {
            return OperationResult<MonitoringIngestResponse>.Success(new MonitoringIngestResponse(false, "monitoring_disabled"));
        }

        var normalizedResult = NormalizeDomain(request.Domain);
        if (normalizedResult.Error is not null)
        {
            return OperationResult<MonitoringIngestResponse>.Success(new MonitoringIngestResponse(false, "domain_not_approved"));
        }
        var domain = normalizedResult.Value!;
        if (!await IsDomainApprovedForEmployeeAsync(employeeResult.Employee!.Id, domain, cancellationToken))
        {
            return OperationResult<MonitoringIngestResponse>.Success(new MonitoringIngestResponse(false, "domain_not_approved"));
        }

        await UpsertSegmentAsync(employeeResult.Employee.Id, MonitoringActivityKind.BusinessDomain, null, null, null, domain, policy.SampleIntervalSeconds, cancellationToken);
        return OperationResult<MonitoringIngestResponse>.Success(new MonitoringIngestResponse(true, null));
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var policy = await dbContext.MonitoringPolicies.AsNoTracking().OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var retentionDays = Math.Clamp(policy?.RetentionDays ?? MonitoringDefaults.RetentionDays, 1, 365);
        var cutoff = UtcNow().AddDays(-retentionDays);
        return await dbContext.MonitoringActivitySegments.Where(x => x.LastObservedAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task UpsertSegmentAsync(
        Guid employeeId,
        MonitoringActivityKind kind,
        string? processName,
        string? applicationName,
        string? windowTitle,
        string? domain,
        int sampleIntervalSeconds,
        CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var mergeCutoff = now.AddSeconds(-(sampleIntervalSeconds * 2 + 10));
        var segment = await dbContext.MonitoringActivitySegments
            .Where(x => x.EmployeeId == employeeId && x.Kind == kind && x.LastObservedAtUtc >= mergeCutoff)
            .OrderByDescending(x => x.LastObservedAtUtc)
            .FirstOrDefaultAsync(x =>
                x.ProcessName == processName &&
                x.WindowTitle == windowTitle &&
                x.Domain == domain,
                cancellationToken);
        if (segment is null)
        {
            segment = new MonitoringActivitySegment
            {
                EmployeeId = employeeId,
                Kind = kind,
                ProcessName = processName,
                ApplicationName = applicationName,
                WindowTitle = windowTitle,
                Domain = domain,
                StartedAtUtc = now,
                LastObservedAtUtc = now,
                SampleIntervalSeconds = sampleIntervalSeconds,
                SampleCount = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            dbContext.MonitoringActivitySegments.Add(segment);
        }
        else
        {
            segment.LastObservedAtUtc = now;
            segment.SampleCount += 1;
            segment.UpdatedAtUtc = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IsDomainApprovedForEmployeeAsync(Guid employeeId, string domain, CancellationToken cancellationToken)
    {
        var rules = await dbContext.ApprovedBusinessDomains.AsNoTracking().Where(x => x.IsActive).ToListAsync(cancellationToken);
        if (rules.Any(rule => DomainMatches(domain, rule.NormalizedDomain, rule.IncludeSubdomains)))
        {
            return true;
        }

        var today = DateOnly.FromDateTime(UtcNow());
        var urls = await dbContext.Set<WebsiteAssignment>()
            .AsNoTracking()
            .Where(x => x.EmployeeId == employeeId && x.IsActive &&
                        (!x.StartsOn.HasValue || x.StartsOn.Value <= today) &&
                        (!x.ExpiresOn.HasValue || x.ExpiresOn.Value >= today))
            .Select(x => x.Url)
            .ToListAsync(cancellationToken);
        return urls.Select(TryGetHost).Any(host => host is not null && string.Equals(host, domain, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyCollection<ApprovedBusinessDomainResponse>> GetAssignedDomainsAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(UtcNow());
        var assignments = await dbContext.Set<WebsiteAssignment>()
            .AsNoTracking()
            .Where(x => x.EmployeeId == employeeId && x.IsActive &&
                        (!x.StartsOn.HasValue || x.StartsOn.Value <= today) &&
                        (!x.ExpiresOn.HasValue || x.ExpiresOn.Value >= today))
            .Select(x => new { x.Id, x.Name, x.Url, x.CreatedAtUtc, x.UpdatedAtUtc })
            .ToListAsync(cancellationToken);
        return assignments
            .Select(x => new { x.Id, x.Name, Domain = TryGetHost(x.Url), x.CreatedAtUtc, x.UpdatedAtUtc })
            .Where(x => x.Domain is not null)
            .GroupBy(x => x.Domain!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(x => new ApprovedBusinessDomainResponse(x.Id, x.Domain!, x.Name, false, true, x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToArray();
    }

    private async Task<(Employee? Employee, ApiOperationError? Error)> ResolveEmployeeAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, new ApiOperationError("actor_required", "A valid authenticated user is required."));
        }

        var employee = await dbContext.Employees
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.UserId == actor.UserId.Value, cancellationToken);
        if (employee is null || !employee.IsActive || !employee.User.IsActive)
        {
            return (null, new ApiOperationError("employee_unavailable", "The authenticated account is not linked to an active employee."));
        }
        return (employee, null);
    }

    private async Task<MonitoringPolicyResponse> GetEffectivePolicyAsync(CancellationToken cancellationToken)
        => ToPolicyResponse(await dbContext.MonitoringPolicies.AsNoTracking().OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken));

    private MonitoringActivityResponse ToActivityResponse(MonitoringActivitySegment entity)
    {
        var duration = Math.Max(entity.SampleIntervalSeconds,
            (int)Math.Ceiling((entity.LastObservedAtUtc - entity.StartedAtUtc).TotalSeconds) + entity.SampleIntervalSeconds);
        return new MonitoringActivityResponse(
            entity.Id,
            entity.EmployeeId,
            entity.Employee.EmployeeCode,
            entity.Employee.FullName,
            entity.Employee.Department?.Name,
            entity.Kind,
            entity.ProcessName,
            entity.ApplicationName,
            entity.WindowTitle,
            entity.Domain,
            entity.StartedAtUtc,
            entity.LastObservedAtUtc,
            entity.SampleCount,
            duration);
    }

    private static MonitoringPolicyResponse ToPolicyResponse(MonitoringPolicy? entity)
        => entity is null
            ? new MonitoringPolicyResponse(null, true, MonitoringDefaults.SampleIntervalSeconds, MonitoringDefaults.RetentionDays, MonitoringDefaults.DisclosureText, null)
            : new MonitoringPolicyResponse(entity.Id, entity.IsEnabled, entity.SampleIntervalSeconds, entity.RetentionDays, entity.DisclosureText, entity.UpdatedAtUtc);

    private static ApprovedApplicationResponse ToApplicationResponse(ApprovedApplication entity)
        => new(entity.Id, entity.ProcessName, entity.DisplayName, entity.CaptureWindowTitle, entity.IsActive, entity.CreatedAtUtc, entity.UpdatedAtUtc);

    private static ApprovedBusinessDomainResponse ToDomainResponse(ApprovedBusinessDomain entity)
        => new(entity.Id, entity.Domain, entity.DisplayName, entity.IncludeSubdomains, entity.IsActive, entity.CreatedAtUtc, entity.UpdatedAtUtc);

    private static (string? Value, ApiOperationError? Error) NormalizeProcessName(string value)
    {
        var raw = value.Trim().ToLowerInvariant();
        if (raw.EndsWith(".exe", StringComparison.Ordinal))
        {
            raw = raw[..^4];
        }
        if (raw.Length is < 1 or > 120 || raw.Contains('/') || raw.Contains('\\') || raw.Contains(':') || raw.Any(char.IsWhiteSpace))
        {
            return (null, new ApiOperationError("invalid_process_name", "Process name must be a simple executable name without a path."));
        }
        return (raw, null);
    }

    private static (string? Value, ApiOperationError? Error) NormalizeDomain(string value)
    {
        var raw = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (raw.Length is < 1 or > 253 || raw.Contains('/') || raw.Contains('\\') || raw.Contains('?') || raw.Contains('#') || raw.Contains(':') || raw.Any(char.IsWhiteSpace))
        {
            return (null, new ApiOperationError("invalid_domain", "Business domain must contain only a hostname, not a URL path, query, port, or scheme."));
        }
        if (!Uri.TryCreate($"https://{raw}", UriKind.Absolute, out var uri) || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns)
        {
            return (null, new ApiOperationError("invalid_domain", "Business domain is not a valid DNS hostname."));
        }
        return (uri.IdnHost.ToLowerInvariant(), null);
    }

    private static string? TryGetHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.IdnHost.ToLowerInvariant()
            : null;

    private static bool DomainMatches(string candidate, string configured, bool includeSubdomains)
        => string.Equals(candidate, configured, StringComparison.OrdinalIgnoreCase) ||
           (includeSubdomains && candidate.EndsWith('.' + configured, StringComparison.OrdinalIgnoreCase));

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
    private static DateTime? NormalizeUtc(DateTime? value)
        => value.HasValue ? (value.Value.Kind == DateTimeKind.Utc ? value.Value : value.Value.ToUniversalTime()) : null;
    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];
}
