using System.ComponentModel.DataAnnotations;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public static class WebsiteWorkReviewStates
{
    public const string NotSubmitted = "NotSubmitted";
    public const string PendingReview = "PendingReview";
    public const string Approved = "Approved";
    public const string CorrectionRequired = "CorrectionRequired";
}

public sealed record WebsiteWorkResponse(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid? EmployeeId,
    string? EmployeeCode,
    string? EmployeeName,
    string Title,
    string? Instructions,
    string Url,
    ProjectTaskStatus Status,
    ProjectTaskPriority Priority,
    DateOnly? DueDate,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public string ReviewState { get; init; } = WebsiteWorkReviewStates.NotSubmitted;
    public string? ReviewComment { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public DateTime? ReviewedAtUtc { get; init; }
}

public sealed class UpsertWebsiteWorkRequest
{
    public Guid ProjectId { get; init; }
    public Guid EmployeeId { get; init; }

    [Required, StringLength(200, MinimumLength = 2)]
    public string Title { get; init; } = string.Empty;

    [StringLength(4000)]
    public string? Instructions { get; init; }

    [Required, StringLength(2048, MinimumLength = 8)]
    public string Url { get; init; } = string.Empty;

    public ProjectTaskPriority Priority { get; init; } = ProjectTaskPriority.Normal;
    public DateOnly? DueDate { get; init; }
}

public sealed class ApproveWebsiteWorkRequest
{
    [StringLength(1000)]
    public string? Comment { get; init; }
}

public sealed class ReopenWebsiteWorkRequest
{
    [Required, StringLength(1000, MinimumLength = 3)]
    public string Comment { get; init; } = string.Empty;
}

public sealed record WebsiteWorkReviewResponse(
    Guid TaskId,
    string State,
    string? Comment,
    DateTime? SubmittedAtUtc,
    DateTime? ReviewedAtUtc);

public sealed record WebsiteWorkCompletionResponse(
    Guid TaskId,
    Guid ProjectId,
    string ProjectName,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string TaskTitle,
    DateTime CompletedAtUtc,
    string Message);

public sealed record WebsiteWorkActiveProgressResponse(
    Guid TaskId,
    Guid ProjectId,
    string ProjectName,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string TaskTitle,
    DateTime StartedAtUtc,
    long ElapsedSeconds,
    DateOnly? DueDate,
    bool IsOverdue);

public sealed record WebsiteWorkEmployeeTodayResponse(
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    int WorkingNow,
    int SubmittedToday,
    int ApprovedToday,
    int ReopenedToday,
    DateTime? LastSubmittedAtUtc,
    DateTime? LastApprovedAtUtc);

public sealed record WebsiteWorkProgressResponse(
    DateTime GeneratedAtUtc,
    int UtcOffsetMinutes,
    int WorkingNow,
    int PendingReview,
    int SubmittedToday,
    int ApprovedToday,
    int ReopenedToday,
    IReadOnlyCollection<WebsiteWorkActiveProgressResponse> ActiveWork,
    IReadOnlyCollection<WebsiteWorkEmployeeTodayResponse> Employees);
