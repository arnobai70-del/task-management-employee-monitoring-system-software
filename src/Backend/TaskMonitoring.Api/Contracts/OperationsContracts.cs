namespace TaskMonitoring.Api.Contracts;

public sealed record AgentHealthReportRequest(
    string MachineName,
    string? DesktopVersion,
    string? ServiceVersion,
    string? UpdaterVersion,
    bool ServiceRunning,
    string? InstalledVersion,
    string? UpdateChannel,
    DateTime? LastSuccessfulUpdateAtUtc,
    DateTime? RolledBackAtUtc);

public sealed record AgentHealthReportResponse(
    DateTime RecordedAtUtc);

public sealed record OperationsServerHealthResponse(
    bool ApiHealthy,
    bool DatabaseHealthy,
    long DatabaseLatencyMilliseconds,
    DateTime ApiStartedAtUtc,
    string ApiVersion);

public sealed record OperationsBackupHealthResponse(
    bool IsKnown,
    DateTime? LastSuccessfulAtUtc,
    long? AgeMinutes,
    bool IsStale,
    string? Label,
    string? ArchiveFile,
    long? SizeBytes);

public sealed record OperationsReleaseHealthResponse(
    bool IsKnown,
    string Channel,
    string? LatestVersion,
    DateTime? PublishedAtUtc);

public sealed record OperationsAgentSummaryResponse(
    int ActiveEmployees,
    int Online,
    int Offline,
    int Healthy,
    int NeedsAttention,
    int DetailedReports,
    int Outdated,
    int ServiceStopped,
    int RolledBack);

public sealed record OperationsAgentHealthResponse(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    Guid? DepartmentId,
    string? DepartmentName,
    bool IsOnline,
    DateTime? LastSeenAtUtc,
    DateTime? DetailedHealthAtUtc,
    string? MachineName,
    string? DesktopVersion,
    string? ServiceVersion,
    string? UpdaterVersion,
    bool? ServiceRunning,
    string? InstalledVersion,
    string? UpdateChannel,
    DateTime? LastSuccessfulUpdateAtUtc,
    DateTime? RolledBackAtUtc,
    bool IsOutdated,
    string Health,
    IReadOnlyCollection<string> Issues);

public sealed record OperationsOverviewResponse(
    DateTime GeneratedAtUtc,
    OperationsServerHealthResponse Server,
    OperationsBackupHealthResponse Backup,
    OperationsReleaseHealthResponse Release,
    OperationsAgentSummaryResponse AgentsSummary,
    IReadOnlyCollection<OperationsAgentHealthResponse> Agents);
