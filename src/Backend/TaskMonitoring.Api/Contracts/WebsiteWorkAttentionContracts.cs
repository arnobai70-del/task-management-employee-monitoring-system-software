using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public enum WebsiteWorkAttentionSeverity
{
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum WebsiteWorkAttentionReasonType
{
    Overdue = 1,
    LongWorking = 2,
    RepeatedCorrection = 3,
    PendingReview = 4
}

public enum WebsiteWorkAttentionDisposition
{
    Active = 1,
    Acknowledged = 2,
    Snoozed = 3,
    FollowUp = 4,
    Resolved = 5
}

public enum WebsiteWorkFollowUpState
{
    Pending = 1,
    Overdue = 2,
    Resolved = 3
}

public enum WebsiteWorkFollowUpRealtimeAction
{
    Assigned = 1,
    Updated = 2,
    Removed = 3,
    Resolved = 4
}

public sealed record WebsiteWorkAttentionThresholdsResponse(
    int LongWorkingMinutes,
    int PendingReviewMinutes,
    int RepeatedCorrectionCount);

public sealed record WebsiteWorkAttentionReasonResponse(
    WebsiteWorkAttentionReasonType Type,
    WebsiteWorkAttentionSeverity Severity,
    string Message);

public sealed record WebsiteWorkAttentionManagementResponse(
    WebsiteWorkAttentionDisposition Disposition,
    bool IsSuppressed,
    DateTime? ActionAtUtc,
    Guid? ActorUserId,
    string? ActorEmail,
    string? Note,
    DateTime? SnoozedUntilUtc,
    Guid? FollowUpOwnerUserId,
    string? FollowUpOwnerEmail,
    string? FollowUpOwnerName,
    DateTime? FollowUpDueAtUtc);

public sealed record WebsiteWorkAttentionItemResponse(
    Guid TaskId,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Title,
    ProjectTaskStatus Status,
    DateOnly? DueDate,
    DateTime? WorkingStartedAtUtc,
    long CurrentWorkingSeconds,
    DateTime? SubmittedAtUtc,
    long PendingReviewSeconds,
    int CorrectionCount,
    WebsiteWorkAttentionSeverity Severity,
    IReadOnlyCollection<WebsiteWorkAttentionReasonResponse> Reasons,
    WebsiteWorkAttentionManagementResponse Management);

public sealed record WebsiteWorkAttentionResponse(
    DateTime GeneratedAtUtc,
    DateOnly LocalDate,
    int UtcOffsetMinutes,
    WebsiteWorkAttentionThresholdsResponse Thresholds,
    int Total,
    int ActiveTotal,
    int ManagedTotal,
    int PendingFollowUpTotal,
    int OverdueFollowUpTotal,
    int Critical,
    int High,
    int Medium,
    IReadOnlyCollection<WebsiteWorkAttentionItemResponse> Items);

public sealed record WebsiteWorkAttentionAcknowledgeRequest(string? Note);

public sealed record WebsiteWorkAttentionSnoozeRequest(int Minutes, string? Note);

public sealed record WebsiteWorkAttentionFollowUpRequest(
    Guid OwnerUserId,
    DateTime DueAtUtc,
    string Note);

public sealed record WebsiteWorkAttentionResolveFollowUpRequest(string? Note);

public sealed record WebsiteWorkAttentionActionResponse(
    Guid TaskId,
    WebsiteWorkAttentionDisposition Disposition,
    DateTime ActionAtUtc,
    string Message);

public sealed record WebsiteWorkAttentionFollowUpOwnerResponse(
    Guid UserId,
    string Email,
    string? FullName);

public sealed record WebsiteWorkFollowUpInboxItemResponse(
    Guid TaskId,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Title,
    ProjectTaskStatus TaskStatus,
    DateOnly? TaskDueDate,
    WebsiteWorkFollowUpState State,
    DateTime AssignedAtUtc,
    Guid? AssignedByUserId,
    string? AssignedByEmail,
    string AssignmentNote,
    DateTime DueAtUtc,
    DateTime? ResolvedAtUtc,
    Guid? ResolvedByUserId,
    string? ResolvedByEmail,
    string? ResolutionNote);

public sealed record WebsiteWorkFollowUpInboxResponse(
    DateTime GeneratedAtUtc,
    int Pending,
    int Overdue,
    int Resolved,
    int TotalCount,
    IReadOnlyCollection<WebsiteWorkFollowUpInboxItemResponse> Items);

public sealed record WebsiteWorkFollowUpRealtimeResponse(
    WebsiteWorkFollowUpRealtimeAction Action,
    Guid TaskId,
    Guid ProjectId,
    string ProjectName,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string Title,
    Guid OwnerUserId,
    string OwnerEmail,
    string? OwnerName,
    DateTime DueAtUtc,
    DateTime OccurredAtUtc,
    string Message);
