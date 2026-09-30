using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public sealed record DashboardOverviewResponse(
    DateTime GeneratedAtUtc,
    DateOnly From,
    DateOnly To,
    WorkforceDashboardMetrics Workforce,
    AttendanceDashboardMetrics Attendance,
    ProjectDashboardMetrics Projects,
    TaskDashboardMetrics Tasks,
    SurveyDashboardMetrics Surveys);

public sealed record WorkforceDashboardMetrics(
    int ActiveEmployees,
    int ActiveDepartments);

public sealed record AttendanceDashboardMetrics(
    int Sessions,
    int DistinctEmployees,
    int CompletedSessions,
    int OpenSessions,
    int LateSessions,
    int EarlyLeaveSessions,
    int TotalBreakMinutes);

public sealed record ProjectDashboardMetrics(
    int Planning,
    int Active,
    int OnHold,
    int Completed,
    int Archived,
    int Overdue);

public sealed record TaskDashboardMetrics(
    int Open,
    int ToDo,
    int InProgress,
    int Blocked,
    int Done,
    int Cancelled,
    int Overdue,
    int UnassignedOpen,
    int CompletedInPeriod);

public sealed record SurveyDashboardMetrics(
    int PublishedForms,
    int ActiveAssignments,
    int PendingReview,
    int ApprovedAssignments,
    int RejectedAssignments,
    int OverdueAssignments,
    int SubmittedInPeriod,
    int ReviewedInPeriod);

public sealed record AttendanceDailyMetricResponse(
    DateOnly WorkDate,
    int Sessions,
    int DistinctEmployees,
    int CompletedSessions,
    int OpenSessions,
    int LateSessions,
    int EarlyLeaveSessions,
    int TotalBreakMinutes);

public sealed record ProjectProgressResponse(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    ProjectStatus Status,
    DateOnly? DueDate,
    int ActiveMembers,
    int TotalTasks,
    int OpenTasks,
    int DoneTasks,
    int BlockedTasks,
    int CancelledTasks,
    int OverdueTasks,
    decimal CompletionPercent);

public sealed record EmployeeWorkloadResponse(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    Guid? DepartmentId,
    string? DepartmentName,
    int OpenTasks,
    int UrgentOpenTasks,
    int OverdueTasks,
    int ActiveSurveyAssignments,
    int OverdueSurveyAssignments,
    int TotalOpenItems);

public sealed record SurveyProgressResponse(
    Guid SurveyFormId,
    Guid ProjectId,
    string ProjectCode,
    string SurveyCode,
    string SurveyName,
    SurveyFormStatus Status,
    int TotalAssignments,
    int Assigned,
    int InProgress,
    int PendingReview,
    int Approved,
    int Rejected,
    int Cancelled,
    int Overdue,
    decimal ApprovalPercent);

public enum WebsiteWorkProductivityGrouping
{
    Day = 1,
    Week = 2
}

public sealed record WebsiteWorkProductivityMetricsResponse(
    int Assigned,
    int Started,
    long WorkingSeconds,
    int Submitted,
    int Approved,
    int Reopened,
    int Overdue,
    decimal CompletionPercent);

public sealed record WebsiteWorkEmployeeProductivityResponse(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    Guid? DepartmentId,
    string? DepartmentName,
    WebsiteWorkProductivityMetricsResponse Metrics);

public sealed record WebsiteWorkProductivityPeriodResponse(
    DateOnly From,
    DateOnly To,
    WebsiteWorkProductivityMetricsResponse Metrics);

public sealed record WebsiteWorkProductivityReportResponse(
    DateTime GeneratedAtUtc,
    DateOnly From,
    DateOnly To,
    int UtcOffsetMinutes,
    WebsiteWorkProductivityGrouping Grouping,
    WebsiteWorkProductivityMetricsResponse Summary,
    IReadOnlyCollection<WebsiteWorkEmployeeProductivityResponse> Employees,
    IReadOnlyCollection<WebsiteWorkProductivityPeriodResponse> Periods);