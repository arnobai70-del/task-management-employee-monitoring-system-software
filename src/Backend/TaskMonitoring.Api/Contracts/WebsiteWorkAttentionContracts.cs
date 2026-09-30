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

public sealed record WebsiteWorkAttentionThresholdsResponse(
    int LongWorkingMinutes,
    int PendingReviewMinutes,
    int RepeatedCorrectionCount);

public sealed record WebsiteWorkAttentionReasonResponse(
    WebsiteWorkAttentionReasonType Type,
    WebsiteWorkAttentionSeverity Severity,
    string Message);

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
    IReadOnlyCollection<WebsiteWorkAttentionReasonResponse> Reasons);

public sealed record WebsiteWorkAttentionResponse(
    DateTime GeneratedAtUtc,
    DateOnly LocalDate,
    int UtcOffsetMinutes,
    WebsiteWorkAttentionThresholdsResponse Thresholds,
    int Total,
    int Critical,
    int High,
    int Medium,
    IReadOnlyCollection<WebsiteWorkAttentionItemResponse> Items);
