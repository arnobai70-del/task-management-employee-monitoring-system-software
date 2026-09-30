using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;

namespace TaskMonitoring.Api.Services;

public sealed record AgentHealthSnapshot(
    Guid EmployeeId,
    DateTime RecordedAtUtc,
    string MachineName,
    string? DesktopVersion,
    string? ServiceVersion,
    string? UpdaterVersion,
    bool ServiceRunning,
    string? InstalledVersion,
    string? UpdateChannel,
    DateTime? LastSuccessfulUpdateAtUtc,
    DateTime? RolledBackAtUtc);

public interface IAgentHealthRegistry
{
    void Set(AgentHealthSnapshot snapshot);
    IReadOnlyDictionary<Guid, AgentHealthSnapshot> Snapshot();
}

public sealed class AgentHealthRegistry : IAgentHealthRegistry
{
    private readonly ConcurrentDictionary<Guid, AgentHealthSnapshot> _items = new();

    public void Set(AgentHealthSnapshot snapshot) => _items[snapshot.EmployeeId] = snapshot;

    public IReadOnlyDictionary<Guid, AgentHealthSnapshot> Snapshot()
        => new Dictionary<Guid, AgentHealthSnapshot>(_items);
}

public interface IOperationsHealthService
{
    Task<OperationResult<AgentHealthReportResponse>> RecordAgentHealthAsync(
        RequestActor actor,
        AgentHealthReportRequest request,
        CancellationToken cancellationToken);

    Task<OperationsOverviewResponse> GetOverviewAsync(
        string? search,
        Guid? departmentId,
        string? health,
        int limit,
        CancellationToken cancellationToken);
}

public sealed class OperationsHealthService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<PresenceOptions> presenceOptions,
    IOptions<OperationsOptions> operationsOptions,
    IAgentHealthRegistry registry) : IOperationsHealthService
{
    private readonly PresenceOptions _presenceOptions = presenceOptions.Value;
    private readonly OperationsOptions _operationsOptions = operationsOptions.Value;
    private static readonly DateTime ProcessStartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public async Task<OperationResult<AgentHealthReportResponse>> RecordAgentHealthAsync(
        RequestActor actor,
        AgentHealthReportRequest request,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return OperationResult<AgentHealthReportResponse>.Invalid("actor_required", "A valid authenticated user is required.");
        }

        var machineName = NormalizeText(request.MachineName, 128);
        if (machineName is null)
        {
            return OperationResult<AgentHealthReportResponse>.Invalid("machine_name_required", "Machine name is required.");
        }

        var employee = await dbContext.Employees
            .AsNoTracking()
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.UserId == actor.UserId.Value, cancellationToken);
        if (employee is null || !employee.IsActive || !employee.User.IsActive)
        {
            return OperationResult<AgentHealthReportResponse>.Invalid("employee_unavailable", "The authenticated account is not linked to an active employee.");
        }

        var now = UtcNow();
        registry.Set(new AgentHealthSnapshot(
            employee.Id,
            now,
            machineName,
            NormalizeText(request.DesktopVersion, 50),
            NormalizeText(request.ServiceVersion, 50),
            NormalizeText(request.UpdaterVersion, 50),
            request.ServiceRunning,
            NormalizeText(request.InstalledVersion, 50),
            NormalizeText(request.UpdateChannel, 30),
            NormalizeUtc(request.LastSuccessfulUpdateAtUtc),
            NormalizeUtc(request.RolledBackAtUtc)));

        return OperationResult<AgentHealthReportResponse>.Success(new AgentHealthReportResponse(now));
    }

    public async Task<OperationsOverviewResponse> GetOverviewAsync(
        string? search,
        Guid? departmentId,
        string? health,
        int limit,
        CancellationToken cancellationToken)
    {
        var now = UtcNow();
        limit = Math.Clamp(limit, 1, 500);
        var server = await BuildServerHealthAsync(cancellationToken);
        var backup = ReadBackupHealth(now);
        var release = ReadReleaseHealth();
        var snapshots = registry.Snapshot();
        var onlineCutoff = now.AddSeconds(-Math.Clamp(_presenceOptions.OnlineThresholdSeconds, 30, 600));
        var detailedCutoff = now.AddMinutes(-Math.Clamp(_operationsOptions.DetailedAgentStaleMinutes, 1, 60));

        var employees = await dbContext.Employees
            .AsNoTracking()
            .Include(x => x.Department)
            .Include(x => x.User)
            .Where(x => x.IsActive && x.User.IsActive)
            .OrderBy(x => x.NormalizedFullName)
            .ThenBy(x => x.NormalizedEmployeeCode)
            .ToListAsync(cancellationToken);

        var employeeIds = employees.Select(x => x.Id).ToArray();
        var presences = await dbContext.EmployeePresences
            .AsNoTracking()
            .Where(x => employeeIds.Contains(x.EmployeeId))
            .ToDictionaryAsync(x => x.EmployeeId, cancellationToken);

        var allAgents = employees.Select(employee =>
        {
            presences.TryGetValue(employee.Id, out var presence);
            snapshots.TryGetValue(employee.Id, out var detail);
            var isOnline = presence is not null && presence.LastSeenAtUtc >= onlineCutoff;
            var detailFresh = detail is not null && detail.RecordedAtUtc >= detailedCutoff;
            var issues = new List<string>();
            var isOutdated = IsOlder(detail?.InstalledVersion ?? detail?.DesktopVersion ?? presence?.ClientVersion, release.LatestVersion);

            if (!isOnline)
            {
                issues.Add("Employee agent is offline or its heartbeat is stale.");
            }
            if (!detailFresh)
            {
                issues.Add(detail is null
                    ? "Detailed desktop/service health has not reported since the API started."
                    : "Detailed desktop/service health report is stale.");
            }
            if (detailFresh && detail is not null && !detail.ServiceRunning)
            {
                issues.Add("Employee Windows Service is not running.");
            }
            if (isOutdated)
            {
                issues.Add($"Installed employee runtime is older than stable release {release.LatestVersion}.");
            }
            if (detailFresh && detail?.RolledBackAtUtc is not null)
            {
                issues.Add("This installation reports a rollback state.");
            }
            if (detailFresh && detail is not null &&
                !string.IsNullOrWhiteSpace(detail.InstalledVersion) &&
                ((!string.IsNullOrWhiteSpace(detail.DesktopVersion) && !VersionsEqual(detail.InstalledVersion, detail.DesktopVersion)) ||
                 (!string.IsNullOrWhiteSpace(detail.ServiceVersion) && !VersionsEqual(detail.InstalledVersion, detail.ServiceVersion))))
            {
                issues.Add("Desktop/service executable versions do not match the recorded installed runtime version.");
            }

            var status = !isOnline
                ? "Offline"
                : detailFresh && detail is not null && !detail.ServiceRunning
                    ? "Critical"
                    : issues.Count == 0
                        ? "Healthy"
                        : "Warning";

            return new OperationsAgentHealthResponse(
                employee.Id,
                employee.EmployeeCode,
                employee.FullName,
                employee.DepartmentId,
                employee.Department?.Name,
                isOnline,
                presence?.LastSeenAtUtc,
                detail?.RecordedAtUtc,
                detail?.MachineName,
                detail?.DesktopVersion ?? presence?.ClientVersion,
                detail?.ServiceVersion,
                detail?.UpdaterVersion,
                detailFresh ? detail?.ServiceRunning : null,
                detail?.InstalledVersion,
                detail?.UpdateChannel,
                detail?.LastSuccessfulUpdateAtUtc,
                detail?.RolledBackAtUtc,
                isOutdated,
                status,
                issues.ToArray());
        }).ToArray();

        var summary = new OperationsAgentSummaryResponse(
            allAgents.Length,
            allAgents.Count(x => x.IsOnline),
            allAgents.Count(x => !x.IsOnline),
            allAgents.Count(x => x.Health == "Healthy"),
            allAgents.Count(x => x.Health != "Healthy"),
            allAgents.Count(x => x.DetailedHealthAtUtc.HasValue && x.DetailedHealthAtUtc.Value >= detailedCutoff),
            allAgents.Count(x => x.IsOutdated),
            allAgents.Count(x => x.ServiceRunning == false),
            allAgents.Count(x => x.RolledBackAtUtc.HasValue));

        IEnumerable<OperationsAgentHealthResponse> filtered = allAgents;
        if (departmentId.HasValue)
        {
            filtered = filtered.Where(x => x.DepartmentId == departmentId.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var value = search.Trim();
            filtered = filtered.Where(x =>
                x.FullName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                x.EmployeeCode.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                (x.DepartmentName?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.MachineName?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        if (!string.IsNullOrWhiteSpace(health))
        {
            filtered = filtered.Where(x => string.Equals(x.Health, health.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var agents = filtered
            .OrderBy(x => HealthOrder(x.Health))
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();

        return new OperationsOverviewResponse(now, server, backup, release, summary, agents);
    }

    private async Task<OperationsServerHealthResponse> BuildServerHealthAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var databaseHealthy = false;
        try
        {
            databaseHealthy = await dbContext.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            databaseHealthy = false;
        }
        stopwatch.Stop();

        return new OperationsServerHealthResponse(
            true,
            databaseHealthy,
            stopwatch.ElapsedMilliseconds,
            ProcessStartedAtUtc,
            NormalizeAssemblyVersion(Assembly.GetExecutingAssembly().GetName().Version));
    }

    private OperationsBackupHealthResponse ReadBackupHealth(DateTime now)
    {
        try
        {
            var path = _operationsOptions.BackupStatusPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new OperationsBackupHealthResponse(false, null, null, true, null, null, null);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("completedAtUtc", out var completedElement) ||
                !DateTime.TryParse(completedElement.GetString(), out var completed))
            {
                return new OperationsBackupHealthResponse(false, null, null, true, null, null, null);
            }

            var completedUtc = NormalizeUtc(completed) ?? completed.ToUniversalTime();
            var ageMinutes = Math.Max(0L, (long)(now - completedUtc).TotalMinutes);
            var stale = ageMinutes > Math.Clamp(_operationsOptions.BackupStaleHours, 1, 168) * 60L;
            var label = root.TryGetProperty("label", out var labelElement) ? labelElement.GetString() : null;
            var archiveFile = root.TryGetProperty("archiveFile", out var archiveElement) ? archiveElement.GetString() : null;
            long? sizeBytes = root.TryGetProperty("sizeBytes", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : null;
            return new OperationsBackupHealthResponse(true, completedUtc, ageMinutes, stale, label, archiveFile, sizeBytes);
        }
        catch
        {
            return new OperationsBackupHealthResponse(false, null, null, true, null, null, null);
        }
    }

    private OperationsReleaseHealthResponse ReadReleaseHealth()
    {
        try
        {
            var path = _operationsOptions.StableReleaseManifestPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new OperationsReleaseHealthResponse(false, "stable", null, null);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var channel = root.TryGetProperty("channel", out var channelElement) && !string.IsNullOrWhiteSpace(channelElement.GetString())
                ? channelElement.GetString()!
                : "stable";
            var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
            DateTime? publishedAtUtc = null;
            if (root.TryGetProperty("publishedAtUtc", out var publishedElement) && DateTime.TryParse(publishedElement.GetString(), out var published))
            {
                publishedAtUtc = NormalizeUtc(published);
            }
            return new OperationsReleaseHealthResponse(!string.IsNullOrWhiteSpace(version), channel, version, publishedAtUtc);
        }
        catch
        {
            return new OperationsReleaseHealthResponse(false, "stable", null, null);
        }
    }

    private static bool IsOlder(string? installed, string? latest)
        => TryVersion(installed, out var installedVersion) &&
           TryVersion(latest, out var latestVersion) &&
           installedVersion < latestVersion;

    private static bool VersionsEqual(string? left, string? right)
        => TryVersion(left, out var leftVersion) && TryVersion(right, out var rightVersion)
            ? leftVersion == rightVersion
            : string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value) || !Version.TryParse(value.Trim(), out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
        return true;
    }

    private static string NormalizeAssemblyVersion(Version? version)
        => version is null ? "0.0.0" : $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";

    private static string? NormalizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static int HealthOrder(string health) => health switch
    {
        "Critical" => 0,
        "Offline" => 1,
        "Warning" => 2,
        _ => 3
    };

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
}
