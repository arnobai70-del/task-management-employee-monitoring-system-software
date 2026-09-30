using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkAttentionService
{
    Task<OperationResult<WebsiteWorkAttentionResponse>> GetAsync(
        int utcOffsetMinutes,
        int limit,
        CancellationToken cancellationToken);
}

public sealed class WebsiteWorkAttentionService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<WebsiteWorkAttentionOptions> options) : IWebsiteWorkAttentionService
{
    private readonly WebsiteWorkAttentionOptions _options = options.Value;

    public async Task<OperationResult<WebsiteWorkAttentionResponse>> GetAsync(
        int utcOffsetMinutes,
        int limit,
        CancellationToken cancellationToken)
    {
        if (utcOffsetMinutes is < -840 or > 840)
        {
            return OperationResult<WebsiteWorkAttentionResponse>.Invalid(
                "utc_offset_invalid",
                "UTC offset must be between -840 and 840 minutes.");
        }

        if (limit is < 1 or > 100)
        {
            return OperationResult<WebsiteWorkAttentionResponse>.Invalid(
                "attention_limit_invalid",
                "Attention result limit must be between 1 and 100.");
        }

        var nowUtc = UtcNow();
        var localDate = DateOnly.FromDateTime(nowUtc + TimeSpan.FromMinutes(utcOffsetMinutes));

        var tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.AssigneeEmployee)
            .Include(x => x.Activities)
            .Where(x =>
                x.AssigneeEmployeeId != null &&
                x.Status != ProjectTaskStatus.Done &&
                x.Status != ProjectTaskStatus.Cancelled &&
                x.Activities.Any(activity => activity.Action == WebsiteWorkService.ConfiguredAction))
            .ToListAsync(cancellationToken);

        var alerts = tasks
            .Select(task => BuildAlert(task, nowUtc, localDate))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Severity)
            .ThenByDescending(item => item.Reasons.Count)
            .ThenByDescending(item => item.PendingReviewSeconds)
            .ThenByDescending(item => item.CurrentWorkingSeconds)
            .ThenBy(item => item.DueDate)
            .ThenBy(item => item.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();

        return OperationResult<WebsiteWorkAttentionResponse>.Success(
            new WebsiteWorkAttentionResponse(
                nowUtc,
                localDate,
                utcOffsetMinutes,
                new WebsiteWorkAttentionThresholdsResponse(
                    _options.LongWorkingMinutes,
                    _options.PendingReviewMinutes,
                    _options.RepeatedCorrectionCount),
                alerts.Length,
                alerts.Count(x => x.Severity == WebsiteWorkAttentionSeverity.Critical),
                alerts.Count(x => x.Severity == WebsiteWorkAttentionSeverity.High),
                alerts.Count(x => x.Severity == WebsiteWorkAttentionSeverity.Medium),
                alerts));
    }

    private WebsiteWorkAttentionItemResponse? BuildAlert(ProjectTask task, DateTime nowUtc, DateOnly localDate)
    {
        if (task.AssigneeEmployee is null)
        {
            return null;
        }

        var ordered = task.Activities.OrderBy(x => x.CreatedAtUtc).ToArray();
        var workingStartedAtUtc = CurrentOpenWorkStartedAt(ordered);
        var currentWorkingSeconds = workingStartedAtUtc.HasValue
            ? Math.Max(0L, (long)(nowUtc - workingStartedAtUtc.Value).TotalSeconds)
            : 0L;

        var submittedAtUtc = task.Status == ProjectTaskStatus.Blocked
            ? ordered
                .Where(x => x.Action == WebsiteWorkReviewService.SubmittedAction)
                .Select(x => (DateTime?)x.CreatedAtUtc)
                .LastOrDefault()
            : null;
        var pendingReviewSeconds = submittedAtUtc.HasValue
            ? Math.Max(0L, (long)(nowUtc - submittedAtUtc.Value).TotalSeconds)
            : 0L;
        var correctionCount = ordered.Count(x => x.Action == WebsiteWorkReviewService.ReopenedAction);

        var reasons = new List<WebsiteWorkAttentionReasonResponse>();
        if (task.DueDate.HasValue && task.DueDate.Value < localDate)
        {
            var overdueDays = localDate.DayNumber - task.DueDate.Value.DayNumber;
            reasons.Add(new WebsiteWorkAttentionReasonResponse(
                WebsiteWorkAttentionReasonType.Overdue,
                WebsiteWorkAttentionSeverity.Critical,
                overdueDays == 1 ? "Overdue by 1 day." : $"Overdue by {overdueDays} days."));
        }

        var longWorkingThresholdSeconds = _options.LongWorkingMinutes * 60L;
        if (task.Status == ProjectTaskStatus.InProgress && currentWorkingSeconds >= longWorkingThresholdSeconds)
        {
            reasons.Add(new WebsiteWorkAttentionReasonResponse(
                WebsiteWorkAttentionReasonType.LongWorking,
                WebsiteWorkAttentionSeverity.High,
                $"Working continuously for {FormatDuration(currentWorkingSeconds)}; threshold is {_options.LongWorkingMinutes} minutes."));
        }

        var pendingReviewThresholdSeconds = _options.PendingReviewMinutes * 60L;
        if (task.Status == ProjectTaskStatus.Blocked && pendingReviewSeconds >= pendingReviewThresholdSeconds)
        {
            reasons.Add(new WebsiteWorkAttentionReasonResponse(
                WebsiteWorkAttentionReasonType.PendingReview,
                WebsiteWorkAttentionSeverity.High,
                $"Waiting for manager review for {FormatDuration(pendingReviewSeconds)}; threshold is {_options.PendingReviewMinutes} minutes."));
        }

        if (correctionCount >= _options.RepeatedCorrectionCount)
        {
            reasons.Add(new WebsiteWorkAttentionReasonResponse(
                WebsiteWorkAttentionReasonType.RepeatedCorrection,
                correctionCount >= _options.RepeatedCorrectionCount * 2
                    ? WebsiteWorkAttentionSeverity.High
                    : WebsiteWorkAttentionSeverity.Medium,
                $"Returned for correction {correctionCount} times; threshold is {_options.RepeatedCorrectionCount}."));
        }

        if (reasons.Count == 0)
        {
            return null;
        }

        var severity = reasons.Max(x => x.Severity);
        return new WebsiteWorkAttentionItemResponse(
            task.Id,
            task.ProjectId,
            task.Project.Code,
            task.Project.Name,
            task.AssigneeEmployee.Id,
            task.AssigneeEmployee.EmployeeCode,
            task.AssigneeEmployee.FullName,
            task.Title,
            task.Status,
            task.DueDate,
            workingStartedAtUtc,
            currentWorkingSeconds,
            submittedAtUtc,
            pendingReviewSeconds,
            correctionCount,
            severity,
            reasons);
    }

    private static DateTime? CurrentOpenWorkStartedAt(IEnumerable<TaskActivity> activities)
    {
        DateTime? openAtUtc = null;
        foreach (var activity in activities.OrderBy(x => x.CreatedAtUtc))
        {
            if (activity.Action == WebsiteWorkService.StartedAction ||
                activity.Action == WebsiteWorkReviewService.ReopenedAction)
            {
                openAtUtc ??= activity.CreatedAtUtc;
                continue;
            }

            if (activity.Action == WebsiteWorkReviewService.SubmittedAction ||
                activity.Action == WebsiteWorkService.CompletedAction)
            {
                openAtUtc = null;
            }
        }

        return openAtUtc;
    }

    private static string FormatDuration(long totalSeconds)
    {
        var totalMinutes = Math.Max(0L, totalSeconds / 60L);
        var hours = totalMinutes / 60L;
        var minutes = totalMinutes % 60L;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
}
