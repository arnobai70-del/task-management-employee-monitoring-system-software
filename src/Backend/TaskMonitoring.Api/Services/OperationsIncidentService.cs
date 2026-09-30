using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;

namespace TaskMonitoring.Api.Services;

public interface IOperationsIncidentService
{
    Task<PagedResponse<OperationsIncidentResponse>> GetAsync(
        OperationsIncidentStatus? status,
        OperationsIncidentSeverity? severity,
        OperationsIncidentKind? kind,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OperationResult<OperationsIncidentResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationsIncidentSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<OperationsIncidentAssigneeResponse>> GetAssigneesAsync(CancellationToken cancellationToken);
    Task<OperationResult<OperationsIncidentResponse>> AcknowledgeAsync(Guid id, OperationsIncidentAcknowledgeRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<OperationsIncidentResponse>> AssignAsync(Guid id, OperationsIncidentAssignRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<OperationsIncidentResponse>> ResolveAsync(Guid id, OperationsIncidentResolveRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task ScanAsync(CancellationToken cancellationToken);
}

public sealed class OperationsIncidentService(
    AppDbContext dbContext,
    IOperationsHealthService operationsHealthService,
    IOptions<OperationsOptions> operationsOptions,
    TimeProvider timeProvider,
    IOperationsIncidentRealtimePublisher realtimePublisher) : IOperationsIncidentService
{
    public const string TargetType = "OperationsIncident";
    public const string DetectedAction = "operations.incident.detected";
    public const string UpdatedAction = "operations.incident.updated";
    public const string ReopenedAction = "operations.incident.reopened";
    public const string AcknowledgedAction = "operations.incident.acknowledged";
    public const string AssignedAction = "operations.incident.assigned";
    public const string ResolvedAction = "operations.incident.resolved";
    public const string AutoResolvedAction = "operations.incident.auto_resolved";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OperationsOptions _options = operationsOptions.Value;

    public async Task<PagedResponse<OperationsIncidentResponse>> GetAsync(
        OperationsIncidentStatus? status,
        OperationsIncidentSeverity? severity,
        OperationsIncidentKind? kind,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        IEnumerable<CurrentIncident> filtered = current.Values;
        if (status.HasValue)
        {
            filtered = filtered.Where(x => x.State.Status == status.Value);
        }
        if (severity.HasValue)
        {
            filtered = filtered.Where(x => x.State.Severity == severity.Value);
        }
        if (kind.HasValue)
        {
            filtered = filtered.Where(x => x.State.Kind == kind.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim();
            filtered = filtered.Where(x =>
                x.State.Title.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                x.State.Message.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                (x.State.EmployeeName?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.State.EmployeeCode?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.State.DepartmentName?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.State.OwnerEmail?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var ordered = filtered
            .OrderBy(x => x.State.Status == OperationsIncidentStatus.Resolved ? 1 : 0)
            .ThenByDescending(x => x.State.Severity)
            .ThenByDescending(x => x.State.LastDetectedAtUtc)
            .ThenBy(x => x.State.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var items = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => ToResponse(x.State, []))
            .ToArray();

        return new PagedResponse<OperationsIncidentResponse>(items, page, pageSize, ordered.Length);
    }

    public async Task<OperationResult<OperationsIncidentResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var current = await LoadCurrentAsync(includeHistory: true, cancellationToken);
        if (!current.TryGetValue(id, out var incident))
        {
            return OperationResult<OperationsIncidentResponse>.NotFound("operations_incident_not_found", "Incident was not found.");
        }

        return OperationResult<OperationsIncidentResponse>.Success(ToResponse(incident.State, incident.History));
    }

    public async Task<OperationsIncidentSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        var states = current.Values.Select(x => x.State).ToArray();
        var today = DateOnly.FromDateTime(UtcNow());
        return new OperationsIncidentSummaryResponse(
            states.Count(x => x.Status == OperationsIncidentStatus.Open),
            states.Count(x => x.Status == OperationsIncidentStatus.Open && x.Severity == OperationsIncidentSeverity.Critical),
            states.Count(x => x.Status == OperationsIncidentStatus.Open && x.Severity == OperationsIncidentSeverity.Warning),
            states.Count(x => x.Status == OperationsIncidentStatus.Acknowledged),
            states.Count(x => x.Status != OperationsIncidentStatus.Resolved && x.OwnerUserId.HasValue),
            states.Count(x => x.ResolvedAtUtc.HasValue && DateOnly.FromDateTime(x.ResolvedAtUtc.Value) == today));
    }

    public async Task<IReadOnlyCollection<OperationsIncidentAssigneeResponse>> GetAssigneesAsync(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .AsNoTracking()
            .Include(x => x.Employee)
            .Where(x => x.IsActive && x.UserRoles.Any(userRole =>
                userRole.Role.IsActive &&
                userRole.Role.RolePermissions.Any(rolePermission =>
                    rolePermission.Permission.Code == PermissionCatalog.OperationsManage)))
            .OrderBy(x => x.Employee != null ? x.Employee.NormalizedFullName : x.NormalizedEmail)
            .ToArrayAsync(cancellationToken);

        return users
            .Select(x => new OperationsIncidentAssigneeResponse(x.Id, x.Email, x.Employee?.FullName))
            .ToArray();
    }

    public Task<OperationResult<OperationsIncidentResponse>> AcknowledgeAsync(
        Guid id,
        OperationsIncidentAcknowledgeRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
        => MutateAsync(id, actor, AcknowledgedAction, request.Note, cancellationToken, state =>
        {
            if (state.Status == OperationsIncidentStatus.Resolved)
            {
                return (null, new ApiOperationError("incident_resolved", "Resolved incidents cannot be acknowledged."));
            }

            var now = UtcNow();
            return (state with
            {
                Status = OperationsIncidentStatus.Acknowledged,
                AcknowledgedAtUtc = state.AcknowledgedAtUtc ?? now
            }, null);
        });

    public async Task<OperationResult<OperationsIncidentResponse>> AssignAsync(
        Guid id,
        OperationsIncidentAssignRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var owner = await dbContext.Users
            .AsNoTracking()
            .Include(x => x.Employee)
            .SingleOrDefaultAsync(x =>
                x.Id == request.OwnerUserId &&
                x.IsActive &&
                x.UserRoles.Any(userRole =>
                    userRole.Role.IsActive &&
                    userRole.Role.RolePermissions.Any(rolePermission =>
                        rolePermission.Permission.Code == PermissionCatalog.OperationsManage)),
                cancellationToken);
        if (owner is null)
        {
            return OperationResult<OperationsIncidentResponse>.Invalid(
                "incident_owner_invalid",
                "Incident owner must be an active user with operations.manage permission.");
        }

        return await MutateAsync(id, actor, AssignedAction, request.Note, cancellationToken, state =>
        {
            if (state.Status == OperationsIncidentStatus.Resolved)
            {
                return (null, new ApiOperationError("incident_resolved", "Resolved incidents cannot be assigned."));
            }

            return (state with
            {
                OwnerUserId = owner.Id,
                OwnerEmail = owner.Email,
                OwnerName = owner.Employee?.FullName
            }, null);
        });
    }

    public Task<OperationResult<OperationsIncidentResponse>> ResolveAsync(
        Guid id,
        OperationsIncidentResolveRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
        => MutateAsync(id, actor, ResolvedAction, request.Note, cancellationToken, state =>
        {
            if (state.Status == OperationsIncidentStatus.Resolved)
            {
                return (null, new ApiOperationError("incident_already_resolved", "Incident is already resolved."));
            }

            var now = UtcNow();
            return (state with
            {
                Status = OperationsIncidentStatus.Resolved,
                ResolvedAtUtc = now,
                ResolutionKind = "Manual"
            }, null);
        });

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var overview = await operationsHealthService.GetOverviewAsync(null, null, null, 500, cancellationToken);
        var signals = BuildSignals(overview, now)
            .ToDictionary(x => (x.Kind, x.SourceKey), x => x);
        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        var currentBySource = current.Values.ToDictionary(x => (x.State.Kind, x.State.SourceKey));
        var changed = new List<(IncidentState State, string Action)>();

        foreach (var signal in signals.Values)
        {
            if (!currentBySource.TryGetValue((signal.Kind, signal.SourceKey), out var existing))
            {
                var created = new IncidentState(
                    DeterministicIncidentId(signal.Kind, signal.SourceKey),
                    signal.Kind,
                    signal.Severity,
                    OperationsIncidentStatus.Open,
                    signal.SourceKey,
                    signal.Title,
                    signal.Message,
                    signal.EmployeeId,
                    signal.EmployeeCode,
                    signal.EmployeeName,
                    signal.DepartmentName,
                    now,
                    now,
                    null,
                    null,
                    null,
                    null,
                    null,
                    1,
                    null);
                AddEvent(created, DetectedAction, null, null, null, null, null, now);
                changed.Add((created, DetectedAction));
                continue;
            }

            var state = existing.State;
            if (state.Status == OperationsIncidentStatus.Resolved)
            {
                var recoveredPreviously = existing.LatestAction == AutoResolvedAction;
                var cooldownElapsed = !state.ResolvedAtUtc.HasValue ||
                                      now - state.ResolvedAtUtc.Value >= TimeSpan.FromMinutes(Math.Clamp(_options.IncidentReopenCooldownMinutes, 1, 1440));
                if (recoveredPreviously || cooldownElapsed)
                {
                    var reopened = state with
                    {
                        Severity = signal.Severity,
                        Status = OperationsIncidentStatus.Open,
                        Title = signal.Title,
                        Message = signal.Message,
                        LastDetectedAtUtc = now,
                        AcknowledgedAtUtc = null,
                        ResolvedAtUtc = null,
                        ResolutionKind = null,
                        OccurrenceCount = state.OccurrenceCount + 1
                    };
                    AddEvent(reopened, ReopenedAction, null, null, null, null, "Health signal detected again.", now);
                    changed.Add((reopened, ReopenedAction));
                }
                continue;
            }

            if (state.Severity != signal.Severity ||
                !string.Equals(state.Title, signal.Title, StringComparison.Ordinal) ||
                !string.Equals(state.Message, signal.Message, StringComparison.Ordinal))
            {
                var updated = state with
                {
                    Severity = signal.Severity,
                    Title = signal.Title,
                    Message = signal.Message,
                    LastDetectedAtUtc = now
                };
                AddEvent(updated, UpdatedAction, null, null, null, null, "Detected health details changed.", now);
                changed.Add((updated, UpdatedAction));
            }
        }

        foreach (var existing in current.Values.Where(x => x.State.Status != OperationsIncidentStatus.Resolved))
        {
            if (signals.ContainsKey((existing.State.Kind, existing.State.SourceKey)))
            {
                continue;
            }

            var resolved = existing.State with
            {
                Status = OperationsIncidentStatus.Resolved,
                ResolvedAtUtc = now,
                ResolutionKind = "Recovered"
            };
            AddEvent(resolved, AutoResolvedAction, null, null, null, null, "The monitored health signal recovered automatically.", now);
            changed.Add((resolved, AutoResolvedAction));
        }

        if (changed.Count == 0)
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (var item in changed)
        {
            await realtimePublisher.PublishAsync(ToChanged(item.State, item.Action, now), cancellationToken);
        }
    }

    private async Task<OperationResult<OperationsIncidentResponse>> MutateAsync(
        Guid id,
        RequestActor actor,
        string action,
        string? note,
        CancellationToken cancellationToken,
        Func<IncidentState, (IncidentState? State, ApiOperationError? Error)> mutation)
    {
        if (!actor.UserId.HasValue)
        {
            return OperationResult<OperationsIncidentResponse>.Invalid("actor_required", "A valid authenticated user is required.");
        }

        var actorUser = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actor.UserId.Value && x.IsActive, cancellationToken);
        if (actorUser is null)
        {
            return OperationResult<OperationsIncidentResponse>.Invalid("actor_invalid", "The authenticated user is unavailable.");
        }

        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        if (!current.TryGetValue(id, out var incident))
        {
            return OperationResult<OperationsIncidentResponse>.NotFound("operations_incident_not_found", "Incident was not found.");
        }

        var mutated = mutation(incident.State);
        if (mutated.Error is not null)
        {
            return OperationResult<OperationsIncidentResponse>.Conflict(mutated.Error.Code, mutated.Error.Message);
        }

        var next = mutated.State!;
        var normalizedNote = NormalizeNote(note);
        var now = UtcNow();
        AddEvent(next, action, actor.UserId, actorUser.Email, actor.IpAddress, actor.UserAgent, normalizedNote, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await realtimePublisher.PublishAsync(ToChanged(next, action, now), cancellationToken);

        var detail = await GetByIdAsync(id, cancellationToken);
        return detail;
    }

    private IReadOnlyCollection<IncidentSignal> BuildSignals(OperationsOverviewResponse overview, DateTime now)
    {
        var signals = new List<IncidentSignal>();

        if (!overview.Backup.IsKnown || overview.Backup.IsStale)
        {
            signals.Add(new IncidentSignal(
                OperationsIncidentKind.BackupStale,
                "postgres-backup",
                OperationsIncidentSeverity.Critical,
                overview.Backup.IsKnown ? "PostgreSQL backup is stale" : "PostgreSQL backup status is missing",
                overview.Backup.IsKnown
                    ? $"The last successful backup is {overview.Backup.AgeMinutes ?? 0} minutes old."
                    : "No successful PostgreSQL backup status is currently available.",
                null, null, null, null));
        }

        if (overview.Server.DatabaseHealthy &&
            overview.Server.DatabaseLatencyMilliseconds >= Math.Clamp(_options.DatabaseLatencyWarningMilliseconds, 100, 60_000))
        {
            signals.Add(new IncidentSignal(
                OperationsIncidentKind.DatabaseDegraded,
                "primary-database",
                OperationsIncidentSeverity.Warning,
                "Database response is degraded",
                $"The current database connectivity probe took {overview.Server.DatabaseLatencyMilliseconds} ms.",
                null, null, null, null));
        }

        var offlineCutoff = now.AddMinutes(-Math.Clamp(_options.AgentOfflineMinutes, 1, 1440));
        var detailCutoff = now.AddMinutes(-Math.Clamp(_options.DetailedAgentStaleMinutes, 1, 60));
        foreach (var agent in overview.Agents)
        {
            if (!agent.IsOnline && (!agent.LastSeenAtUtc.HasValue || agent.LastSeenAtUtc.Value <= offlineCutoff))
            {
                signals.Add(new IncidentSignal(
                    OperationsIncidentKind.AgentOffline,
                    agent.EmployeeId.ToString("N"),
                    OperationsIncidentSeverity.Warning,
                    $"Agent offline: {agent.FullName}",
                    agent.LastSeenAtUtc.HasValue
                        ? $"No employee heartbeat has been received since {agent.LastSeenAtUtc.Value:O}."
                        : "This employee agent has not recorded a heartbeat.",
                    agent.EmployeeId, agent.EmployeeCode, agent.FullName, agent.DepartmentName));
            }

            if (agent.ServiceRunning == false)
            {
                signals.Add(new IncidentSignal(
                    OperationsIncidentKind.ServiceStopped,
                    agent.EmployeeId.ToString("N"),
                    OperationsIncidentSeverity.Critical,
                    $"Employee service stopped: {agent.FullName}",
                    "The latest detailed agent report says TaskMonitoringEmployeeService is not running.",
                    agent.EmployeeId, agent.EmployeeCode, agent.FullName, agent.DepartmentName));
            }

            if (agent.IsOutdated)
            {
                signals.Add(new IncidentSignal(
                    OperationsIncidentKind.OutdatedRuntime,
                    agent.EmployeeId.ToString("N"),
                    OperationsIncidentSeverity.Warning,
                    $"Employee runtime outdated: {agent.FullName}",
                    $"Installed/runtime version {agent.InstalledVersion ?? agent.DesktopVersion ?? "unknown"} is older than stable release {overview.Release.LatestVersion ?? "unknown"}.",
                    agent.EmployeeId, agent.EmployeeCode, agent.FullName, agent.DepartmentName));
            }

            if (agent.RolledBackAtUtc.HasValue &&
                agent.DetailedHealthAtUtc.HasValue &&
                agent.DetailedHealthAtUtc.Value >= detailCutoff)
            {
                signals.Add(new IncidentSignal(
                    OperationsIncidentKind.RollbackDetected,
                    agent.EmployeeId.ToString("N"),
                    OperationsIncidentSeverity.Critical,
                    $"Rollback detected: {agent.FullName}",
                    $"The employee installation reports a rollback at {agent.RolledBackAtUtc.Value:O}.",
                    agent.EmployeeId, agent.EmployeeCode, agent.FullName, agent.DepartmentName));
            }
        }

        return signals;
    }

    private async Task<Dictionary<Guid, CurrentIncident>> LoadCurrentAsync(bool includeHistory, CancellationToken cancellationToken)
    {
        var logs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(x => x.TargetType == TargetType && x.TargetId != null && x.Action.StartsWith("operations.incident."))
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToArrayAsync(cancellationToken);

        var result = new Dictionary<Guid, CurrentIncident>();
        foreach (var group in logs.GroupBy(x => x.TargetId, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(group.Key, out var id))
            {
                continue;
            }

            var parsed = group
                .Select(log => (Log: log, Event: ParseEvent(log.MetadataJson)))
                .Where(x => x.Event is not null)
                .Select(x => (x.Log, Event: x.Event!))
                .ToArray();
            if (parsed.Length == 0)
            {
                continue;
            }

            var latest = parsed[^1];
            var history = includeHistory
                ? parsed.Select(x => new OperationsIncidentEventResponse(
                    FriendlyAction(x.Log.Action),
                    x.Log.CreatedAtUtc,
                    x.Log.ActorUserId,
                    x.Event.ActorEmail,
                    x.Event.Note)).Reverse().ToArray()
                : [];
            result[id] = new CurrentIncident(latest.Event.State, latest.Log.Action, history);
        }

        return result;
    }

    private void AddEvent(
        IncidentState state,
        string action,
        Guid? actorUserId,
        string? actorEmail,
        string? ipAddress,
        string? userAgent,
        string? note,
        DateTime atUtc)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorUserId,
            Action = action,
            TargetType = TargetType,
            TargetId = state.Id.ToString("D"),
            MetadataJson = JsonSerializer.Serialize(new StoredIncidentEvent(state, note, actorEmail), JsonOptions),
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAtUtc = atUtc
        });
    }

    private static StoredIncidentEvent? ParseEvent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredIncidentEvent>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OperationsIncidentResponse ToResponse(
        IncidentState state,
        IReadOnlyCollection<OperationsIncidentEventResponse> history)
        => new(
            state.Id,
            state.Kind,
            state.Severity,
            state.Status,
            state.SourceKey,
            state.Title,
            state.Message,
            state.EmployeeId,
            state.EmployeeCode,
            state.EmployeeName,
            state.DepartmentName,
            state.FirstDetectedAtUtc,
            state.LastDetectedAtUtc,
            state.AcknowledgedAtUtc,
            state.ResolvedAtUtc,
            state.OwnerUserId,
            state.OwnerEmail,
            state.OwnerName,
            state.OccurrenceCount,
            state.ResolutionKind,
            history);

    private static OperationsIncidentChangedResponse ToChanged(IncidentState state, string action, DateTime atUtc)
        => new(state.Id, state.Kind, state.Severity, state.Status, state.Title, state.Message, FriendlyAction(action), atUtc);

    private static string FriendlyAction(string action) => action switch
    {
        DetectedAction => "Detected",
        UpdatedAction => "Updated",
        ReopenedAction => "Reopened",
        AcknowledgedAction => "Acknowledged",
        AssignedAction => "Assigned",
        ResolvedAction => "Resolved",
        AutoResolvedAction => "Auto resolved",
        _ => action
    };

    private static Guid DeterministicIncidentId(OperationsIncidentKind kind, string sourceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"operations-incident|{kind}|{sourceKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string? NormalizeNote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 1000 ? trimmed : trimmed[..1000];
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record CurrentIncident(
        IncidentState State,
        string LatestAction,
        IReadOnlyCollection<OperationsIncidentEventResponse> History);

    private sealed record IncidentSignal(
        OperationsIncidentKind Kind,
        string SourceKey,
        OperationsIncidentSeverity Severity,
        string Title,
        string Message,
        Guid? EmployeeId,
        string? EmployeeCode,
        string? EmployeeName,
        string? DepartmentName);

    private sealed record IncidentState(
        Guid Id,
        OperationsIncidentKind Kind,
        OperationsIncidentSeverity Severity,
        OperationsIncidentStatus Status,
        string SourceKey,
        string Title,
        string Message,
        Guid? EmployeeId,
        string? EmployeeCode,
        string? EmployeeName,
        string? DepartmentName,
        DateTime FirstDetectedAtUtc,
        DateTime LastDetectedAtUtc,
        DateTime? AcknowledgedAtUtc,
        DateTime? ResolvedAtUtc,
        Guid? OwnerUserId,
        string? OwnerEmail,
        string? OwnerName,
        int OccurrenceCount,
        string? ResolutionKind);

    private sealed record StoredIncidentEvent(
        IncidentState State,
        string? Note,
        string? ActorEmail);
}
