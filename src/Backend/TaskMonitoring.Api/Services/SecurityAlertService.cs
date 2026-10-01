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

public interface ISecurityAlertService
{
    Task<PagedResponse<SecurityAlertResponse>> GetAsync(
        SecurityAlertStatus? status,
        SecurityAlertSeverity? severity,
        SecurityAlertKind? kind,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OperationResult<SecurityAlertResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<SecurityAlertSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<SecurityAlertAssigneeResponse>> GetAssigneesAsync(CancellationToken cancellationToken);
    Task<OperationResult<SecurityAlertResponse>> AcknowledgeAsync(Guid id, SecurityAlertAcknowledgeRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<SecurityAlertResponse>> AssignAsync(Guid id, SecurityAlertAssignRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<SecurityAlertResponse>> ResolveAsync(Guid id, SecurityAlertResolveRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task ScanAsync(CancellationToken cancellationToken);
}

public sealed class SecurityAlertService(
    AppDbContext dbContext,
    IOptions<SecurityObservabilityOptions> options,
    TimeProvider timeProvider,
    ISecurityAlertRealtimePublisher realtimePublisher) : ISecurityAlertService
{
    public const string TargetType = "SecurityAlert";
    public const string DetectedAction = "security.alert.detected";
    public const string UpdatedAction = "security.alert.updated";
    public const string ReopenedAction = "security.alert.reopened";
    public const string AcknowledgedAction = "security.alert.acknowledged";
    public const string AssignedAction = "security.alert.assigned";
    public const string EscalatedAction = "security.alert.escalated";
    public const string ResolvedAction = "security.alert.resolved";
    public const string AutoResolvedAction = "security.alert.auto_resolved";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SecurityObservabilityOptions _options = options.Value;

    public async Task<PagedResponse<SecurityAlertResponse>> GetAsync(
        SecurityAlertStatus? status,
        SecurityAlertSeverity? severity,
        SecurityAlertKind? kind,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        IEnumerable<CurrentAlert> filtered = current.Values;

        if (status.HasValue) filtered = filtered.Where(x => x.State.Status == status.Value);
        if (severity.HasValue) filtered = filtered.Where(x => x.State.Severity == severity.Value);
        if (kind.HasValue) filtered = filtered.Where(x => x.State.Kind == kind.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim();
            filtered = filtered.Where(x =>
                x.State.Title.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                x.State.Message.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                x.State.SourceKey.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                (x.State.OwnerEmail?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.State.OwnerName?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var ordered = filtered
            .OrderBy(x => x.State.Status == SecurityAlertStatus.Resolved ? 1 : 0)
            .ThenByDescending(x => x.State.Status == SecurityAlertStatus.Escalated)
            .ThenByDescending(x => x.State.Severity)
            .ThenByDescending(x => x.State.LastDetectedAtUtc)
            .ThenBy(x => x.State.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        return new PagedResponse<SecurityAlertResponse>(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(x => ToResponse(x.State, [])).ToArray(),
            page,
            pageSize,
            ordered.Length);
    }

    public async Task<OperationResult<SecurityAlertResponse>> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var current = await LoadCurrentAsync(includeHistory: true, cancellationToken);
        return current.TryGetValue(id, out var alert)
            ? OperationResult<SecurityAlertResponse>.Success(ToResponse(alert.State, alert.History))
            : OperationResult<SecurityAlertResponse>.NotFound("security_alert_not_found", "Security alert was not found.");
    }

    public async Task<SecurityAlertSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        var states = current.Values.Select(x => x.State).ToArray();
        var today = DateOnly.FromDateTime(UtcNow());
        return new SecurityAlertSummaryResponse(
            states.Count(x => x.Status == SecurityAlertStatus.Open),
            states.Count(x => x.Status == SecurityAlertStatus.Acknowledged),
            states.Count(x => x.Status == SecurityAlertStatus.Escalated),
            states.Count(x => x.Status != SecurityAlertStatus.Resolved && x.Severity == SecurityAlertSeverity.Critical),
            states.Count(x => x.Status != SecurityAlertStatus.Resolved && x.OwnerUserId.HasValue),
            states.Count(x => x.ResolvedAtUtc.HasValue && DateOnly.FromDateTime(x.ResolvedAtUtc.Value) == today));
    }

    public async Task<IReadOnlyCollection<SecurityAlertAssigneeResponse>> GetAssigneesAsync(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .AsNoTracking()
            .Include(x => x.Employee)
            .Where(x =>
                x.IsActive &&
                x.UserRoles.Any(userRole =>
                    userRole.Role.IsActive &&
                    userRole.Role.RolePermissions.Any(rolePermission => rolePermission.Permission.Code == PermissionCatalog.AuditRead)) &&
                x.UserRoles.Any(userRole =>
                    userRole.Role.IsActive &&
                    userRole.Role.RolePermissions.Any(rolePermission => rolePermission.Permission.Code == PermissionCatalog.SecurityAlertsManage)))
            .OrderBy(x => x.Employee != null ? x.Employee.NormalizedFullName : x.NormalizedEmail)
            .ToArrayAsync(cancellationToken);

        return users.Select(x => new SecurityAlertAssigneeResponse(x.Id, x.Email, x.Employee?.FullName)).ToArray();
    }

    public Task<OperationResult<SecurityAlertResponse>> AcknowledgeAsync(
        Guid id,
        SecurityAlertAcknowledgeRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
        => MutateAsync(id, actor, AcknowledgedAction, request.Note, cancellationToken, state =>
        {
            if (state.Status == SecurityAlertStatus.Resolved)
            {
                return (null, new ApiOperationError("security_alert_resolved", "Resolved security alerts cannot be acknowledged."));
            }

            var now = UtcNow();
            return (state with
            {
                Status = SecurityAlertStatus.Acknowledged,
                AcknowledgedAtUtc = state.AcknowledgedAtUtc ?? now,
                Revision = state.Revision + 1
            }, null);
        });

    public async Task<OperationResult<SecurityAlertResponse>> AssignAsync(
        Guid id,
        SecurityAlertAssignRequest request,
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
                    userRole.Role.RolePermissions.Any(rolePermission => rolePermission.Permission.Code == PermissionCatalog.AuditRead)) &&
                x.UserRoles.Any(userRole =>
                    userRole.Role.IsActive &&
                    userRole.Role.RolePermissions.Any(rolePermission => rolePermission.Permission.Code == PermissionCatalog.SecurityAlertsManage)),
                cancellationToken);
        if (owner is null)
        {
            return OperationResult<SecurityAlertResponse>.Invalid(
                "security_alert_owner_invalid",
                "Alert owner must be an active user with audit.read and security.alerts.manage permissions.");
        }

        return await MutateAsync(id, actor, AssignedAction, request.Note, cancellationToken, state =>
        {
            if (state.Status == SecurityAlertStatus.Resolved)
            {
                return (null, new ApiOperationError("security_alert_resolved", "Resolved security alerts cannot be assigned."));
            }

            return (state with
            {
                OwnerUserId = owner.Id,
                OwnerEmail = owner.Email,
                OwnerName = owner.Employee?.FullName,
                Revision = state.Revision + 1
            }, null);
        });
    }

    public Task<OperationResult<SecurityAlertResponse>> ResolveAsync(
        Guid id,
        SecurityAlertResolveRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
        => MutateAsync(id, actor, ResolvedAction, request.Note, cancellationToken, state =>
        {
            if (state.Status == SecurityAlertStatus.Resolved)
            {
                return (null, new ApiOperationError("security_alert_already_resolved", "Security alert is already resolved."));
            }

            var now = UtcNow();
            return (state with
            {
                Status = SecurityAlertStatus.Resolved,
                ResolvedAtUtc = now,
                ResolutionKind = "Manual",
                Revision = state.Revision + 1
            }, null);
        });

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var signals = await BuildSignalsAsync(now, cancellationToken);
        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        var currentBySource = current.Values.ToDictionary(x => (x.State.Kind, x.State.SourceKey));
        var changed = new List<(SecurityAlertState State, string Action, DateTime AtUtc)>();
        var reopenCooldown = TimeSpan.FromMinutes(Math.Clamp(_options.AlertReopenCooldownMinutes, 1, 1440));
        var escalationAfter = TimeSpan.FromMinutes(Math.Clamp(_options.AlertEscalationAfterMinutes, 1, 1440));

        foreach (var signal in signals.Values)
        {
            if (!currentBySource.TryGetValue((signal.Kind, signal.SourceKey), out var existing))
            {
                var created = new SecurityAlertState(
                    DeterministicAlertId(signal.Kind, signal.SourceKey),
                    signal.Kind,
                    signal.Severity,
                    SecurityAlertStatus.Open,
                    signal.SourceKey,
                    signal.Title,
                    signal.Message,
                    signal.EventCount,
                    signal.FirstSeenAtUtc,
                    signal.LastSeenAtUtc,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    1,
                    null,
                    1);
                AddEvent(created, DetectedAction, null, null, null, null, null, now);
                currentBySource[(signal.Kind, signal.SourceKey)] = new CurrentAlert(created, DetectedAction, []);
                changed.Add((created, DetectedAction, now));
                continue;
            }

            var state = existing.State;
            if (state.Status == SecurityAlertStatus.Resolved)
            {
                var cooldownElapsed = !state.ResolvedAtUtc.HasValue || now - state.ResolvedAtUtc.Value >= reopenCooldown;
                var newSignalAfterResolution = !state.ResolvedAtUtc.HasValue || signal.LastSeenAtUtc > state.ResolvedAtUtc.Value;
                if (cooldownElapsed && newSignalAfterResolution)
                {
                    var reopened = state with
                    {
                        Severity = signal.Severity,
                        Status = SecurityAlertStatus.Open,
                        Title = signal.Title,
                        Message = signal.Message,
                        EventCount = signal.EventCount,
                        FirstDetectedAtUtc = signal.FirstSeenAtUtc,
                        LastDetectedAtUtc = signal.LastSeenAtUtc,
                        AcknowledgedAtUtc = null,
                        EscalatedAtUtc = null,
                        ResolvedAtUtc = null,
                        ResolutionKind = null,
                        OccurrenceCount = state.OccurrenceCount + 1,
                        Revision = state.Revision + 1
                    };
                    AddEvent(reopened, ReopenedAction, null, null, null, null, "Security signal detected again after cooldown.", now);
                    currentBySource[(signal.Kind, signal.SourceKey)] = new CurrentAlert(reopened, ReopenedAction, []);
                    changed.Add((reopened, ReopenedAction, now));
                }
                continue;
            }

            var next = state;
            var action = string.Empty;
            if (state.Severity != signal.Severity ||
                state.EventCount != signal.EventCount ||
                state.LastDetectedAtUtc != signal.LastSeenAtUtc ||
                !string.Equals(state.Title, signal.Title, StringComparison.Ordinal) ||
                !string.Equals(state.Message, signal.Message, StringComparison.Ordinal))
            {
                next = state with
                {
                    Severity = signal.Severity,
                    Title = signal.Title,
                    Message = signal.Message,
                    EventCount = signal.EventCount,
                    LastDetectedAtUtc = signal.LastSeenAtUtc,
                    Revision = state.Revision + 1
                };
                action = UpdatedAction;
            }

            if (next.Status == SecurityAlertStatus.Open &&
                next.Severity == SecurityAlertSeverity.Critical &&
                now - next.FirstDetectedAtUtc >= escalationAfter)
            {
                next = next with
                {
                    Status = SecurityAlertStatus.Escalated,
                    EscalatedAtUtc = now,
                    Revision = next.Revision + 1
                };
                action = EscalatedAction;
            }

            if (!string.IsNullOrEmpty(action))
            {
                AddEvent(next, action, null, null, null, null,
                    action == EscalatedAction ? "Critical security alert exceeded the acknowledgement SLA." : null,
                    now);
                currentBySource[(signal.Kind, signal.SourceKey)] = new CurrentAlert(next, action, []);
                changed.Add((next, action, now));
            }
        }

        foreach (var existing in current.Values.Where(x => x.State.Status != SecurityAlertStatus.Resolved))
        {
            if (signals.ContainsKey((existing.State.Kind, existing.State.SourceKey))) continue;

            var resolved = existing.State with
            {
                Status = SecurityAlertStatus.Resolved,
                ResolvedAtUtc = now,
                ResolutionKind = "Recovered",
                Revision = existing.State.Revision + 1
            };
            AddEvent(resolved, AutoResolvedAction, null, null, null, null, "The correlated security signal is no longer active.", now);
            changed.Add((resolved, AutoResolvedAction, now));
        }

        if (changed.Count == 0) return;

        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (var item in changed)
        {
            await realtimePublisher.PublishAsync(ToChanged(item.State, item.Action, item.AtUtc), cancellationToken);
        }
    }

    private async Task<Dictionary<(SecurityAlertKind Kind, string SourceKey), SecuritySignal>> BuildSignalsAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddMinutes(-Math.Clamp(_options.CorrelationWindowMinutes, 1, 1440));
        var logs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(x =>
                x.CreatedAtUtc >= cutoff &&
                x.CreatedAtUtc <= now &&
                (x.Action == "auth.login.failed" ||
                 x.Action == "auth.login.blocked" ||
                 x.Action == "auth.refresh.reuse_detected" ||
                 x.Action == SecurityObservabilityService.RateLimitRejectedAction))
            .ToArrayAsync(cancellationToken);

        var signals = new Dictionary<(SecurityAlertKind Kind, string SourceKey), SecuritySignal>();
        var failedThreshold = Math.Clamp(_options.FailedLoginThreshold, 2, 1000);
        var rateThreshold = Math.Clamp(_options.RateLimitThreshold, 2, 1000);

        foreach (var group in logs
                     .Where(x => x.Action is "auth.login.failed" or "auth.login.blocked")
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.IpAddress) ? "unknown" : x.IpAddress!, StringComparer.OrdinalIgnoreCase)
                     .Where(x => x.Count() >= failedThreshold))
        {
            var count = group.Count();
            var severity = count >= failedThreshold * 2 ? SecurityAlertSeverity.Critical : SecurityAlertSeverity.Warning;
            var sourceKey = $"login:{group.Key}";
            signals[(SecurityAlertKind.FailedLoginBurst, sourceKey)] = new SecuritySignal(
                SecurityAlertKind.FailedLoginBurst,
                severity,
                sourceKey,
                $"Repeated failed sign-ins from {group.Key}",
                $"{count} failed or blocked sign-in events were recorded from this source within the last {_options.CorrelationWindowMinutes} minutes.",
                count,
                group.Min(x => x.CreatedAtUtc),
                group.Max(x => x.CreatedAtUtc));
        }

        foreach (var group in logs
                     .Where(x => x.Action == SecurityObservabilityService.RateLimitRejectedAction)
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.IpAddress) ? "unknown" : x.IpAddress!, StringComparer.OrdinalIgnoreCase)
                     .Where(x => x.Count() >= rateThreshold))
        {
            var count = group.Count();
            var severity = count >= rateThreshold * 2 ? SecurityAlertSeverity.Critical : SecurityAlertSeverity.Warning;
            var sourceKey = $"rate:{group.Key}";
            signals[(SecurityAlertKind.RateLimitBurst, sourceKey)] = new SecuritySignal(
                SecurityAlertKind.RateLimitBurst,
                severity,
                sourceKey,
                $"Rate-limit burst from {group.Key}",
                $"{count} rate-limit rejections were recorded from this source within the last {_options.CorrelationWindowMinutes} minutes.",
                count,
                group.Min(x => x.CreatedAtUtc),
                group.Max(x => x.CreatedAtUtc));
        }

        foreach (var group in logs
                     .Where(x => x.Action == "auth.refresh.reuse_detected")
                     .GroupBy(x => x.ActorUserId.HasValue
                         ? $"user:{x.ActorUserId.Value:N}"
                         : $"ip:{x.IpAddress ?? "unknown"}", StringComparer.OrdinalIgnoreCase))
        {
            var count = group.Count();
            var sourceKey = $"refresh:{group.Key}";
            signals[(SecurityAlertKind.RefreshTokenReuse, sourceKey)] = new SecuritySignal(
                SecurityAlertKind.RefreshTokenReuse,
                SecurityAlertSeverity.Critical,
                sourceKey,
                "Refresh-token reuse detected",
                $"{count} refresh-token reuse event{(count == 1 ? "" : "s")} were detected for {group.Key}; active sessions are revoked by the authentication service.",
                count,
                group.Min(x => x.CreatedAtUtc),
                group.Max(x => x.CreatedAtUtc));
        }

        return signals;
    }

    private async Task<OperationResult<SecurityAlertResponse>> MutateAsync(
        Guid id,
        RequestActor actor,
        string action,
        string? note,
        CancellationToken cancellationToken,
        Func<SecurityAlertState, (SecurityAlertState? State, ApiOperationError? Error)> mutation)
    {
        if (!actor.UserId.HasValue)
        {
            return OperationResult<SecurityAlertResponse>.Invalid("actor_required", "A valid authenticated user is required.");
        }

        var actorUser = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == actor.UserId.Value && x.IsActive, cancellationToken);
        if (actorUser is null)
        {
            return OperationResult<SecurityAlertResponse>.Invalid("actor_invalid", "The authenticated user is unavailable.");
        }

        var current = await LoadCurrentAsync(includeHistory: false, cancellationToken);
        if (!current.TryGetValue(id, out var alert))
        {
            return OperationResult<SecurityAlertResponse>.NotFound("security_alert_not_found", "Security alert was not found.");
        }

        var mutated = mutation(alert.State);
        if (mutated.Error is not null)
        {
            return OperationResult<SecurityAlertResponse>.Conflict(mutated.Error.Code, mutated.Error.Message);
        }

        var next = mutated.State!;
        var now = UtcNow();
        AddEvent(next, action, actor.UserId, actorUser.Email, actor.IpAddress, actor.UserAgent, NormalizeNote(note), now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await realtimePublisher.PublishAsync(ToChanged(next, action, now), cancellationToken);
        return await GetByIdAsync(id, cancellationToken);
    }

    private async Task<Dictionary<Guid, CurrentAlert>> LoadCurrentAsync(bool includeHistory, CancellationToken cancellationToken)
    {
        var logs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(x => x.TargetType == TargetType && x.TargetId != null && x.Action.StartsWith("security.alert."))
            .ToArrayAsync(cancellationToken);

        var result = new Dictionary<Guid, CurrentAlert>();
        foreach (var group in logs.GroupBy(x => x.TargetId!, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(group.Key, out var id)) continue;
            var parsed = group
                .Select(log => (Log: log, Event: ParseEvent(log.MetadataJson)))
                .Where(x => x.Event is not null)
                .Select(x => (x.Log, Event: x.Event!))
                .OrderBy(x => x.Event.State.Revision)
                .ThenBy(x => x.Log.CreatedAtUtc)
                .ThenBy(x => x.Log.Id)
                .ToArray();
            if (parsed.Length == 0) continue;

            var latest = parsed[^1];
            var history = includeHistory
                ? parsed.OrderByDescending(x => x.Event.State.Revision)
                    .ThenByDescending(x => x.Log.CreatedAtUtc)
                    .Select(x => new SecurityAlertEventResponse(
                        FriendlyAction(x.Log.Action),
                        x.Log.CreatedAtUtc,
                        x.Log.ActorUserId,
                        x.Event.ActorEmail,
                        x.Event.Note))
                    .ToArray()
                : [];
            result[id] = new CurrentAlert(latest.Event.State, latest.Log.Action, history);
        }
        return result;
    }

    private void AddEvent(
        SecurityAlertState state,
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
            MetadataJson = JsonSerializer.Serialize(new StoredSecurityAlertEvent(state, note, actorEmail), JsonOptions),
            IpAddress = ipAddress,
            UserAgent = userAgent,
            CreatedAtUtc = atUtc
        });
    }

    private static StoredSecurityAlertEvent? ParseEvent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StoredSecurityAlertEvent>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static SecurityAlertResponse ToResponse(SecurityAlertState state, IReadOnlyCollection<SecurityAlertEventResponse> history)
        => new(
            state.Id,
            state.Kind,
            state.Severity,
            state.Status,
            state.SourceKey,
            state.Title,
            state.Message,
            state.EventCount,
            state.FirstDetectedAtUtc,
            state.LastDetectedAtUtc,
            state.AcknowledgedAtUtc,
            state.EscalatedAtUtc,
            state.ResolvedAtUtc,
            state.OwnerUserId,
            state.OwnerEmail,
            state.OwnerName,
            state.OccurrenceCount,
            state.ResolutionKind,
            history);

    private static SecurityAlertChangedResponse ToChanged(SecurityAlertState state, string action, DateTime atUtc)
        => new(state.Id, state.Kind, state.Severity, state.Status, state.Title, state.Message, FriendlyAction(action), atUtc);

    private static string FriendlyAction(string action) => action switch
    {
        DetectedAction => "Detected",
        UpdatedAction => "Updated",
        ReopenedAction => "Reopened",
        AcknowledgedAction => "Acknowledged",
        AssignedAction => "Assigned",
        EscalatedAction => "Escalated",
        ResolvedAction => "Resolved",
        AutoResolvedAction => "Auto resolved",
        _ => action
    };

    private static Guid DeterministicAlertId(SecurityAlertKind kind, string sourceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"security-alert|{kind}|{sourceKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string? NormalizeNote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= 1000 ? trimmed : trimmed[..1000];
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record CurrentAlert(
        SecurityAlertState State,
        string LatestAction,
        IReadOnlyCollection<SecurityAlertEventResponse> History);

    private sealed record SecuritySignal(
        SecurityAlertKind Kind,
        SecurityAlertSeverity Severity,
        string SourceKey,
        string Title,
        string Message,
        int EventCount,
        DateTime FirstSeenAtUtc,
        DateTime LastSeenAtUtc);

    private sealed record SecurityAlertState(
        Guid Id,
        SecurityAlertKind Kind,
        SecurityAlertSeverity Severity,
        SecurityAlertStatus Status,
        string SourceKey,
        string Title,
        string Message,
        int EventCount,
        DateTime FirstDetectedAtUtc,
        DateTime LastDetectedAtUtc,
        DateTime? AcknowledgedAtUtc,
        DateTime? EscalatedAtUtc,
        DateTime? ResolvedAtUtc,
        Guid? OwnerUserId,
        string? OwnerEmail,
        string? OwnerName,
        int OccurrenceCount,
        string? ResolutionKind,
        int Revision);

    private sealed record StoredSecurityAlertEvent(
        SecurityAlertState State,
        string? Note,
        string? ActorEmail);
}

public sealed class SecurityAlertHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SecurityObservabilityOptions> options,
    ILogger<SecurityAlertHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.AlertScanIntervalSeconds, 30, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISecurityAlertService>().ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Security alert scan failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
