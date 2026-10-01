namespace TaskMonitoring.Api.Contracts;

public sealed record SecurityOverviewResponse(
    DateTime GeneratedAtUtc,
    DateTime FromUtc,
    DateTime ToUtc,
    int FailedLogins,
    int BlockedLogins,
    int SuccessfulLogins,
    int RefreshReuseDetections,
    int RateLimitRejections,
    int PrivilegedActions,
    int UniqueSecuritySourceIps);

public sealed record SecurityEventResponse(
    Guid Id,
    Guid? ActorUserId,
    string? ActorEmail,
    string Action,
    string TargetType,
    string? TargetId,
    string? IpAddress,
    string? UserAgent,
    string? MetadataJson,
    DateTime CreatedAtUtc);

public sealed record SecuritySourceSummaryResponse(
    string Source,
    int FailedLogins,
    int BlockedLogins,
    int RefreshReuseDetections,
    int RateLimitRejections,
    DateTime LastSeenAtUtc);

public sealed record SecurityCorrelationResponse(
    string Kind,
    string Source,
    string Severity,
    int EventCount,
    string Message,
    DateTime FirstSeenAtUtc,
    DateTime LastSeenAtUtc);

public sealed record PrivilegedAuditActionResponse(
    Guid Id,
    Guid ActorUserId,
    string? ActorEmail,
    string Action,
    string TargetType,
    string? TargetId,
    string? IpAddress,
    DateTime CreatedAtUtc);

public sealed record AuditIntegrityResponse(
    DateTime FromUtc,
    DateTime ToUtc,
    int RecordCount,
    string Sha256,
    bool Truncated,
    int MinimumRetentionDays,
    double CoverageDays,
    bool HasMinimumRetentionCoverage,
    DateTime? OldestRecordAtUtc,
    DateTime? NewestRecordAtUtc,
    int StructurallyInvalidRecords,
    string Status);

public sealed record SecurityDashboardResponse(
    SecurityOverviewResponse Overview,
    IReadOnlyCollection<SecurityEventResponse> RecentEvents,
    IReadOnlyCollection<SecuritySourceSummaryResponse> TopSources,
    IReadOnlyCollection<SecurityCorrelationResponse> Correlations,
    IReadOnlyCollection<PrivilegedAuditActionResponse> RecentPrivilegedActions,
    AuditIntegrityResponse Integrity);
