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
    FollowUp = 4
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

public sealed record WebsiteWorkAttentionActionResponse(
    Guid TaskId,
    WebsiteWorkAttentionDisposition Disposition,
    DateTime ActionAtUtc,
    string Message);

public sealed record WebsiteWorkAttentionFollowUpOwnerResponse(
    Guid UserId,
    string Email,
    string? FullName);
