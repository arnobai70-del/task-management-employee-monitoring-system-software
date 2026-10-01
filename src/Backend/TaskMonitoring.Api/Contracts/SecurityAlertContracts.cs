namespace TaskMonitoring.Api.Contracts;

public enum SecurityAlertKind
{
    FailedLoginBurst = 1,
    RateLimitBurst = 2,
    RefreshTokenReuse = 3
}

public enum SecurityAlertSeverity
{
    Warning = 1,
    Critical = 2
}

public enum SecurityAlertStatus
{
    Open = 1,
    Acknowledged = 2,
    Escalated = 3,
    Resolved = 4
}

public sealed record SecurityAlertEventResponse(
    string Action,
    DateTime AtUtc,
    Guid? ActorUserId,
    string? ActorEmail,
    string? Note);

public sealed record SecurityAlertResponse(
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
    IReadOnlyCollection<SecurityAlertEventResponse> History);

public sealed record SecurityAlertSummaryResponse(
    int Open,
    int Acknowledged,
    int Escalated,
    int CriticalActive,
    int Assigned,
    int ResolvedToday);

public sealed record SecurityAlertAssigneeResponse(
    Guid UserId,
    string Email,
    string? Name);

public sealed record SecurityAlertAcknowledgeRequest(string? Note);
public sealed record SecurityAlertAssignRequest(Guid OwnerUserId, string? Note);
public sealed record SecurityAlertResolveRequest(string? Note);

public sealed record SecurityAlertChangedResponse(
    Guid Id,
    SecurityAlertKind Kind,
    SecurityAlertSeverity Severity,
    SecurityAlertStatus Status,
    string Title,
    string Message,
    string Action,
    DateTime OccurredAtUtc);
