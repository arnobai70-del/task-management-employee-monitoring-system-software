using System.Text.Json;
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

    Task<OperationResult<WebsiteWorkAttentionResponse>> GetAsync(
        int utcOffsetMinutes,
        int limit,
        bool includeSuppressed,
        CancellationToken cancellationToken);
}

public sealed class WebsiteWorkAttentionService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<WebsiteWorkAttentionOptions> options) : IWebsiteWorkAttentionService
{
    private readonly WebsiteWorkAttentionOptions _options = options.Value;

    public Task<OperationResult<WebsiteWorkAttentionResponse>> GetAsync(
        int utcOffsetMinutes,
        int limit,
        CancellationToken cancellationToken)
        => GetAsync(utcOffsetMinutes, limit, false, cancellationToken);

    public async Task<OperationResult<WebsiteWorkAttentionResponse>> GetAsync(
        int utcOffsetMinutes,
        int limit,
        bool includeSuppressed,
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

        var allAlerts = tasks
            .Select(task => BuildAlert(task, nowUtc, localDate))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.Management.IsSuppressed)
            .ThenByDescending(item => item.Severity)
            .ThenByDescending(item => item.Reasons.Count)
            .ThenByDescending(item => item.PendingReviewSeconds)
            .ThenByDescending(item => item.CurrentWorkingSeconds)
            .ThenBy(item => item.DueDate)
            .ThenBy(item => item.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var activeAlerts = allAlerts.Where(item => !item.Management.IsSuppressed).ToArray();
        var resultSet = includeSuppressed ? allAlerts : activeAlerts;
        var visibleAlerts = resultSet.Take(limit).ToArray();
        var pendingFollowUps = allAlerts.Count(item =>
            item.Management.Disposition == WebsiteWorkAttentionDisposition.FollowUp &&
            item.Management.IsSuppressed);
        var overdueFollowUps = allAlerts.Count(item =>
            item.Management.Disposition == WebsiteWorkAttentionDisposition.FollowUp &&
            !item.Management.IsSuppressed);

        return OperationResult<WebsiteWorkAttentionResponse>.Success(
            new WebsiteWorkAttentionResponse(
                nowUtc,
                localDate,
                utcOffsetMinutes,
                new WebsiteWorkAttentionThresholdsResponse(
                    _options.LongWorkingMinutes,
                    _options.PendingReviewMinutes,
                    _options.RepeatedCorrectionCount),
                allAlerts.Length,
                activeAlerts.Length,
                allAlerts.Length - activeAlerts.Length,
                pendingFollowUps,
                overdueFollowUps,
                activeAlerts.Count(x => x.Severity == WebsiteWorkAttentionSeverity.Critical),
                activeAlerts.Count(x => x.Severity == WebsiteWorkAttentionSeverity.High),
                activeAlerts.Count(x => x.Severity == WebsiteWorkAttentionSeverity.Medium),
                visibleAlerts));
    }

    private WebsiteWorkAttentionItemResponse? BuildAlert(ProjectTask task, DateTime nowUtc, DateOnly localDate)
    {
        if (task.AssigneeEmployee is null)
        {
            return null;
        }

        var ordered = task.Activities.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArray();
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
            reasons,
            BuildManagement(ordered, nowUtc));
    }

    private static WebsiteWorkAttentionManagementResponse BuildManagement(
        IReadOnlyCollection<TaskActivity> activities,
        DateTime nowUtc)
    {
        var lifecycleAtUtc = activities
            .Where(activity => IsLifecycleAction(activity.Action))
            .Select(activity => (DateTime?)activity.CreatedAtUtc)
            .Max();

        var action = activities
            .Where(activity => IsManagementAction(activity.Action))
            .Where(activity => !lifecycleAtUtc.HasValue || activity.CreatedAtUtc > lifecycleAtUtc.Value)
            .OrderByDescending(activity => activity.CreatedAtUtc)
            .ThenByDescending(activity => activity.Id)
            .FirstOrDefault();

        if (action is null)
        {
            return ActiveManagement();
        }

        var details = ParseDetails(action.DetailsJson);
        var actorUserId = ReadGuid(details, "actorUserId") ?? action.ActorUserId;
        var actorEmail = ReadString(details, "actorEmail");
        var note = ReadString(details, "note");

        if (action.Action == WebsiteWorkAttentionActionService.AcknowledgedAction)
        {
            return new WebsiteWorkAttentionManagementResponse(
                WebsiteWorkAttentionDisposition.Acknowledged,
                true,
                action.CreatedAtUtc,
                actorUserId,
                actorEmail,
                note,
                null,
                null,
                null,
                null,
                null);
        }

        if (action.Action == WebsiteWorkAttentionActionService.SnoozedAction)
        {
            var untilUtc = ReadDateTime(details, "snoozedUntilUtc");
            return new WebsiteWorkAttentionManagementResponse(
                WebsiteWorkAttentionDisposition.Snoozed,
                untilUtc.HasValue && untilUtc.Value > nowUtc,
                action.CreatedAtUtc,
                actorUserId,
                actorEmail,
                note,
                untilUtc,
                null,
                null,
                null,
                null);
        }

        var followUpDueAtUtc = ReadDateTime(details, "followUpDueAtUtc");
        if (action.Action == WebsiteWorkAttentionActionService.FollowUpResolvedAction)
        {
            return new WebsiteWorkAttentionManagementResponse(
                WebsiteWorkAttentionDisposition.Resolved,
                true,
                action.CreatedAtUtc,
                actorUserId,
                actorEmail,
                note,
                null,
                ReadGuid(details, "followUpOwnerUserId"),
                ReadString(details, "followUpOwnerEmail"),
                ReadString(details, "followUpOwnerName"),
                followUpDueAtUtc);
        }

        return new WebsiteWorkAttentionManagementResponse(
            WebsiteWorkAttentionDisposition.FollowUp,
            followUpDueAtUtc.HasValue && followUpDueAtUtc.Value > nowUtc,
            action.CreatedAtUtc,
            actorUserId,
            actorEmail,
            note,
            null,
            ReadGuid(details, "followUpOwnerUserId"),
            ReadString(details, "followUpOwnerEmail"),
            ReadString(details, "followUpOwnerName"),
            followUpDueAtUtc);
    }

    private static WebsiteWorkAttentionManagementResponse ActiveManagement()
        => new(
            WebsiteWorkAttentionDisposition.Active,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    private static bool IsLifecycleAction(string action)
        => action == WebsiteWorkService.ConfiguredAction ||
           action == WebsiteWorkService.StartedAction ||
           action == WebsiteWorkReviewService.SubmittedAction ||
           action == WebsiteWorkReviewService.ReopenedAction ||
           action == WebsiteWorkReviewService.ApprovedAction ||
           action == WebsiteWorkService.CompletedAction;

    private static bool IsManagementAction(string action)
        => action == WebsiteWorkAttentionActionService.AcknowledgedAction ||
           action == WebsiteWorkAttentionActionService.SnoozedAction ||
           action == WebsiteWorkAttentionActionService.FollowUpAssignedAction ||
           action == WebsiteWorkAttentionActionService.FollowUpResolvedAction;

    private static JsonElement? ParseDetails(string detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? details, string propertyName)
    {
        if (!details.HasValue ||
            !details.Value.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static Guid? ReadGuid(JsonElement? details, string propertyName)
        => Guid.TryParse(ReadString(details, propertyName), out var value) ? value : null;

    private static DateTime? ReadDateTime(JsonElement? details, string propertyName)
    {
        if (!details.HasValue || !details.Value.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    private static DateTime? CurrentOpenWorkStartedAt(IEnumerable<TaskActivity> activities)
    {
        DateTime? openAtUtc = null;
        foreach (var activity in activities.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id))
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
