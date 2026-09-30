namespace TaskMonitoring.Api.Contracts;

public enum OperationsIncidentSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3
}

public enum OperationsIncidentStatus
{
    Open = 1,
    Acknowledged = 2,
    Resolved = 3
}

public enum OperationsIncidentKind
{
    AgentOffline = 1,
    ServiceStopped = 2,
    OutdatedRuntime = 3,
    RollbackDetected = 4,
    BackupStale = 5,
    DatabaseDegraded = 6,
    UpdateFailed = 7
}

public sealed record OperationsIncidentEventResponse(
    string Action,
    DateTime AtUtc,
    Guid? ActorUserId,
    string? ActorEmail,
    string? Note);

public sealed record OperationsIncidentResponse(
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
    IReadOnlyCollection<OperationsIncidentEventResponse> History);

public sealed record OperationsIncidentSummaryResponse(
    int Open,
    int OpenCritical,
    int OpenWarning,
    int Acknowledged,
    int Assigned,
    int ResolvedToday);

public sealed record OperationsIncidentAssigneeResponse(
    Guid UserId,
    string Email,
    string? Name);

public sealed record OperationsIncidentAcknowledgeRequest(string? Note);
public sealed record OperationsIncidentAssignRequest(Guid OwnerUserId, string? Note);
public sealed record OperationsIncidentResolveRequest(string? Note);

public sealed record OperationsIncidentChangedResponse(
    Guid Id,
    OperationsIncidentKind Kind,
    OperationsIncidentSeverity Severity,
    OperationsIncidentStatus Status,
    string Title,
    string Message,
    string Action,
    DateTime OccurredAtUtc);
