using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public enum WebsiteWorkTimelineEventType
{
    Assigned = 1,
    Started = 2,
    Submitted = 3,
    CorrectionRequested = 4,
    Approved = 5,
    Completed = 6
}

public sealed record WebsiteWorkTimelineEventResponse(
    WebsiteWorkTimelineEventType Type,
    DateTime AtUtc,
    string Title,
    string? Detail,
    string? ActorEmail);

public sealed record WebsiteWorkTimelineItemResponse(
    Guid TaskId,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string Title,
    ProjectTaskStatus Status,
    DateOnly? DueDate,
    DateTime AssignedAtUtc,
    DateTime? FirstStartedAtUtc,
    DateTime? LastSubmittedAtUtc,
    DateTime? ApprovedAtUtc,
    int CorrectionCount,
    long WorkingSecondsInPeriod,
    long TotalWorkingSeconds,
    bool OverdueAtPeriodEnd,
    IReadOnlyCollection<WebsiteWorkTimelineEventResponse> Events);

public sealed record WebsiteWorkEmployeeTimelineResponse(
    DateTime GeneratedAtUtc,
    DateOnly From,
    DateOnly To,
    int UtcOffsetMinutes,
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    Guid? DepartmentId,
    string? DepartmentName,
    IReadOnlyCollection<WebsiteWorkTimelineItemResponse> Items);
