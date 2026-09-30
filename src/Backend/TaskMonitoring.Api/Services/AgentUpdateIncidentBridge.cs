using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IAgentUpdateIncidentBridge
{
    Task ScanAsync(CancellationToken cancellationToken);
}

public sealed class AgentUpdateIncidentBridge(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<OperationsOptions> operationsOptions,
    IOperationsIncidentRealtimePublisher realtimePublisher) : IAgentUpdateIncidentBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OperationsOptions _options = operationsOptions.Value;

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        var statusLogs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(x =>
                x.TargetType == AgentUpdateService.DeviceTargetType &&
                x.TargetId != null &&
                x.Action == AgentUpdateService.DeviceStatusAction)
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToArrayAsync(cancellationToken);

        var latestStatuses = new Dictionary<string, UpdateStatusSignal>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in statusLogs)
        {
            if (!Guid.TryParse(log.TargetId, out var deviceId) || !TryParseStatus(log.MetadataJson, out var parsed))
            {
                continue;
            }

            var sourceKey = SourceKey(deviceId, parsed.RolloutId);
            latestStatuses[sourceKey] = parsed with { DeviceId = deviceId };
        }

        if (latestStatuses.Count == 0)
        {
            return;
        }

        var employeeIds = latestStatuses.Values
            .Where(x => x.EmployeeId.HasValue)
            .Select(x => x.EmployeeId!.Value)
            .Distinct()
            .ToArray();
        var employees = await dbContext.Employees
            .AsNoTracking()
            .Include(x => x.Department)
            .Where(x => employeeIds.Contains(x.Id))
            .ToDictionaryAsync(
                x => x.Id,
                x => new EmployeeInfo(x.EmployeeCode, x.FullName, x.Department != null ? x.Department.Name : null),
                cancellationToken);

        var incidentLogs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(x =>
                x.TargetType == OperationsIncidentService.TargetType &&
                x.TargetId != null &&
                x.Action.StartsWith("operations.incident."))
            .ToArrayAsync(cancellationToken);
        var current = BuildCurrentIncidents(incidentLogs);
        var now = UtcNow();
        var changed = new List<(BridgeIncidentState State, string Action, DateTime AtUtc)>();

        foreach (var pair in latestStatuses)
        {
            var signal = pair.Value;
            current.TryGetValue(pair.Key, out var existing);

            if (signal.Status is AgentUpdateAssignmentStatus.Failed or AgentUpdateAssignmentStatus.RolledBack)
            {
                employees.TryGetValue(signal.EmployeeId ?? Guid.Empty, out var employee);
                var name = employee?.FullName ?? signal.MachineName;
                var severity = OperationsIncidentSeverity.Critical;
                var title = signal.Status == AgentUpdateAssignmentStatus.RolledBack
                    ? $"Agent update rolled back: {name}"
                    : $"Agent update failed: {name}";
                var message = string.IsNullOrWhiteSpace(signal.Message)
                    ? $"Central rollout {signal.RolloutId:D} could not install target version {signal.TargetVersion}."
                    : signal.Message!;

                if (existing is null)
                {
                    var state = new BridgeIncidentState(
                        DeterministicIncidentId(pair.Key),
                        OperationsIncidentKind.UpdateFailed,
                        severity,
                        OperationsIncidentStatus.Open,
                        pair.Key,
                        title,
                        message,
                        signal.EmployeeId,
                        employee?.EmployeeCode,
                        employee?.FullName,
                        employee?.DepartmentName,
                        signal.AtUtc,
                        signal.AtUtc,
                        null,
                        null,
                        null,
                        null,
                        null,
                        1,
                        null,
                        1);
                    AddIncidentEvent(state, OperationsIncidentService.DetectedAction, null, signal.AtUtc);
                    current[pair.Key] = new CurrentIncident(state, OperationsIncidentService.DetectedAction);
                    changed.Add((state, OperationsIncidentService.DetectedAction, signal.AtUtc));
                    continue;
                }

                var currentState = existing.State;
                if (currentState.Status == OperationsIncidentStatus.Resolved)
                {
                    var recoveredPreviously = existing.LatestAction == OperationsIncidentService.AutoResolvedAction;
                    var cooldownElapsed = !currentState.ResolvedAtUtc.HasValue ||
                        now - currentState.ResolvedAtUtc.Value >= TimeSpan.FromMinutes(
                            Math.Clamp(_options.IncidentReopenCooldownMinutes, 1, 1440));
                    if (!recoveredPreviously && !cooldownElapsed)
                    {
                        continue;
                    }

                    var reopened = currentState with
                    {
                        Severity = severity,
                        Status = OperationsIncidentStatus.Open,
                        Title = title,
                        Message = message,
                        LastDetectedAtUtc = signal.AtUtc,
                        AcknowledgedAtUtc = null,
                        ResolvedAtUtc = null,
                        ResolutionKind = null,
                        OccurrenceCount = currentState.OccurrenceCount + 1,
                        Revision = currentState.Revision + 1
                    };
                    AddIncidentEvent(reopened, OperationsIncidentService.ReopenedAction, "Central update failure was reported again.", signal.AtUtc);
                    current[pair.Key] = new CurrentIncident(reopened, OperationsIncidentService.ReopenedAction);
                    changed.Add((reopened, OperationsIncidentService.ReopenedAction, signal.AtUtc));
                    continue;
                }

                if (signal.AtUtc <= currentState.LastDetectedAtUtc &&
                    string.Equals(currentState.Title, title, StringComparison.Ordinal) &&
                    string.Equals(currentState.Message, message, StringComparison.Ordinal))
                {
                    continue;
                }

                var updated = currentState with
                {
                    Severity = severity,
                    Title = title,
                    Message = message,
                    LastDetectedAtUtc = signal.AtUtc,
                    Revision = currentState.Revision + 1
                };
                AddIncidentEvent(updated, OperationsIncidentService.UpdatedAction, "A new central update failure report was received.", signal.AtUtc);
                current[pair.Key] = new CurrentIncident(updated, OperationsIncidentService.UpdatedAction);
                changed.Add((updated, OperationsIncidentService.UpdatedAction, signal.AtUtc));
                continue;
            }

            if (signal.Status == AgentUpdateAssignmentStatus.Installed &&
                existing is not null &&
                existing.State.Status != OperationsIncidentStatus.Resolved)
            {
                var resolved = existing.State with
                {
                    Status = OperationsIncidentStatus.Resolved,
                    ResolvedAtUtc = signal.AtUtc,
                    ResolutionKind = "Recovered",
                    Revision = existing.State.Revision + 1
                };
                AddIncidentEvent(resolved, OperationsIncidentService.AutoResolvedAction, "The target version was installed successfully.", signal.AtUtc);
                current[pair.Key] = new CurrentIncident(resolved, OperationsIncidentService.AutoResolvedAction);
                changed.Add((resolved, OperationsIncidentService.AutoResolvedAction, signal.AtUtc));
            }
        }

        if (changed.Count == 0)
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (var item in changed)
        {
            await realtimePublisher.PublishAsync(new OperationsIncidentChangedResponse(
                item.State.Id,
                item.State.Kind,
                item.State.Severity,
                item.State.Status,
                item.State.Title,
                item.State.Message,
                FriendlyAction(item.Action),
                item.AtUtc), cancellationToken);
        }
    }

    private Dictionary<string, CurrentIncident> BuildCurrentIncidents(IEnumerable<AuditLog> logs)
    {
        var result = new Dictionary<string, CurrentIncident>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in logs.GroupBy(x => x.TargetId!, StringComparer.OrdinalIgnoreCase))
        {
            var parsed = group
                .Select(log => (Log: log, Event: ParseIncidentEvent(log.MetadataJson)))
                .Where(x => x.Event is not null && x.Event.State.Kind == OperationsIncidentKind.UpdateFailed)
                .Select(x => (x.Log, Event: x.Event!))
                .OrderBy(x => x.Event.State.Revision)
                .ThenBy(x => x.Log.CreatedAtUtc)
                .ThenBy(x => x.Log.Id)
                .ToArray();
            if (parsed.Length == 0)
            {
                continue;
            }

            var latest = parsed[^1];
            result[latest.Event.State.SourceKey] = new CurrentIncident(latest.Event.State, latest.Log.Action);
        }
        return result;
    }

    private void AddIncidentEvent(BridgeIncidentState state, string action, string? note, DateTime atUtc)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            Action = action,
            TargetType = OperationsIncidentService.TargetType,
            TargetId = state.Id.ToString("D"),
            MetadataJson = JsonSerializer.Serialize(new BridgeStoredIncidentEvent(state, note, null), JsonOptions),
            CreatedAtUtc = atUtc
        });
    }

    private static bool TryParseStatus(string? json, out UpdateStatusSignal signal)
    {
        signal = default!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var statusElement) || statusElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetGuid(statusElement, "rolloutId", out var rolloutId) ||
                !TryGetAssignmentStatus(statusElement, "status", out var status))
            {
                return false;
            }

            Guid? employeeId = null;
            if (TryGetGuid(statusElement, "employeeId", out var employee))
            {
                employeeId = employee;
            }

            var machineName = GetString(statusElement, "machineName") ?? "Unknown device";
            var targetVersion = GetString(statusElement, "targetVersion") ?? "unknown";
            var message = GetString(statusElement, "message");
            var atUtc = DateTime.UtcNow;
            if (statusElement.TryGetProperty("atUtc", out var atElement) &&
                atElement.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(atElement.GetString(), out var parsedAt))
            {
                atUtc = NormalizeUtc(parsedAt);
            }

            signal = new UpdateStatusSignal(Guid.Empty, rolloutId, status, employeeId, machineName, targetVersion, message, atUtc);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetAssignmentStatus(JsonElement parent, string property, out AgentUpdateAssignmentStatus status)
    {
        status = default;
        if (!parent.TryGetProperty(property, out var element))
        {
            return false;
        }
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(AgentUpdateAssignmentStatus), numeric))
        {
            status = (AgentUpdateAssignmentStatus)numeric;
            return true;
        }
        if (element.ValueKind == JsonValueKind.String &&
            Enum.TryParse(element.GetString(), ignoreCase: true, out AgentUpdateAssignmentStatus parsed))
        {
            status = parsed;
            return true;
        }
        return false;
    }

    private static bool TryGetGuid(JsonElement parent, string property, out Guid value)
    {
        value = Guid.Empty;
        if (!parent.TryGetProperty(property, out var element)) return false;
        return element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out value);
    }

    private static string? GetString(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static BridgeStoredIncidentEvent? ParseIncidentEvent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<BridgeStoredIncidentEvent>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static string SourceKey(Guid deviceId, Guid rolloutId)
        => $"update:{deviceId:N}:{rolloutId:N}";

    private static Guid DeterministicIncidentId(string sourceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"operations-incident|{OperationsIncidentKind.UpdateFailed}|{sourceKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string FriendlyAction(string action) => action switch
    {
        OperationsIncidentService.DetectedAction => "Detected",
        OperationsIncidentService.UpdatedAction => "Updated",
        OperationsIncidentService.ReopenedAction => "Reopened",
        OperationsIncidentService.AutoResolvedAction => "Auto resolved",
        _ => action
    };

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private sealed record UpdateStatusSignal(
        Guid DeviceId,
        Guid RolloutId,
        AgentUpdateAssignmentStatus Status,
        Guid? EmployeeId,
        string MachineName,
        string TargetVersion,
        string? Message,
        DateTime AtUtc);

    private sealed record EmployeeInfo(string EmployeeCode, string FullName, string? DepartmentName);
    private sealed record CurrentIncident(BridgeIncidentState State, string LatestAction);

    private sealed record BridgeIncidentState(
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
        string? ResolutionKind,
        int Revision);

    private sealed record BridgeStoredIncidentEvent(
        BridgeIncidentState State,
        string? Note,
        string? ActorEmail);
}

public sealed class AgentUpdateIncidentHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<OperationsOptions> options,
    ILogger<AgentUpdateIncidentHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.IncidentScanIntervalSeconds, 30, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IAgentUpdateIncidentBridge>().ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Central agent-update incident scan failed.");
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
