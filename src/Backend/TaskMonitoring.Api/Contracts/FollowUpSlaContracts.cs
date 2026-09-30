namespace TaskMonitoring.Api.Contracts;

public enum FollowUpSlaGrouping
{
    Day = 1,
    Week = 2
}

public sealed record FollowUpSlaMetricsResponse(
    int Due,
    int Resolved,
    int SlaMet,
    int SlaBreached,
    int Escalated,
    int OpenOverdue,
    decimal AverageResolutionMinutes,
    decimal AverageOverdueMinutes,
    decimal SlaMetPercent);

public sealed record FollowUpSlaManagerResponse(
    Guid OwnerUserId,
    string OwnerEmail,
    string? OwnerName,
    Guid? DepartmentId,
    string? DepartmentName,
    FollowUpSlaMetricsResponse Metrics,
    bool RepeatedEscalation);

public sealed record FollowUpSlaDepartmentResponse(
    Guid? DepartmentId,
    string DepartmentName,
    int Managers,
    int RepeatedEscalationManagers,
    FollowUpSlaMetricsResponse Metrics);

public sealed record FollowUpSlaPeriodResponse(
    DateOnly From,
    DateOnly To,
    FollowUpSlaMetricsResponse Metrics);

public sealed record FollowUpSlaAnalyticsResponse(
    DateTime GeneratedAtUtc,
    DateOnly From,
    DateOnly To,
    int UtcOffsetMinutes,
    FollowUpSlaGrouping Grouping,
    int EscalationAfterMinutes,
    int RepeatedEscalationManagers,
    FollowUpSlaMetricsResponse Summary,
    IReadOnlyCollection<FollowUpSlaManagerResponse> Managers,
    IReadOnlyCollection<FollowUpSlaDepartmentResponse> Departments,
    IReadOnlyCollection<FollowUpSlaPeriodResponse> Periods);
