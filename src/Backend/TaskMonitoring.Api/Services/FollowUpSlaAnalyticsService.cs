using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IFollowUpSlaAnalyticsService
{
    Task<OperationResult<FollowUpSlaAnalyticsResponse>> GetAsync(
        DateOnly? from,
        DateOnly? to,
        int utcOffsetMinutes,
        FollowUpSlaGrouping grouping,
        CancellationToken cancellationToken);
}

public sealed class FollowUpSlaAnalyticsService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<FollowUpReminderOptions> reminderOptions) : IFollowUpSlaAnalyticsService
{
    private const int MaximumReportDays = 366;
    private const int MaximumFollowUpLeadDays = 30;
    private readonly FollowUpReminderOptions _reminderOptions = reminderOptions.Value;

    public async Task<OperationResult<FollowUpSlaAnalyticsResponse>> GetAsync(
        DateOnly? from,
        DateOnly? to,
        int utcOffsetMinutes,
        FollowUpSlaGrouping grouping,
        CancellationToken cancellationToken)
    {
        if (utcOffsetMinutes is < -840 or > 840)
        {
            return OperationResult<FollowUpSlaAnalyticsResponse>.Invalid(
                "utc_offset_invalid",
                "UTC offset must be between -840 and 840 minutes.");
        }

        if (!Enum.IsDefined(grouping))
        {
            return OperationResult<FollowUpSlaAnalyticsResponse>.Invalid(
                "follow_up_sla_grouping_invalid",
                "SLA grouping must be Day or Week.");
        }

        var nowUtc = UtcNow();
        var offset = TimeSpan.FromMinutes(utcOffsetMinutes);
        var localToday = DateOnly.FromDateTime(nowUtc + offset);
        var reportTo = to ?? localToday;
        var reportFrom = from ?? reportTo.AddDays(-29);

        if (reportFrom > reportTo)
        {
            return OperationResult<FollowUpSlaAnalyticsResponse>.Invalid(
                "report_range_invalid",
                "The report start date cannot be after the end date.");
        }

        if (reportTo > localToday)
        {
            return OperationResult<FollowUpSlaAnalyticsResponse>.Invalid(
                "report_range_future",
                "The follow-up SLA report cannot include future dates.");
        }

        var totalDays = reportTo.DayNumber - reportFrom.DayNumber + 1;
        if (totalDays > MaximumReportDays)
        {
            return OperationResult<FollowUpSlaAnalyticsResponse>.Invalid(
                "report_range_too_large",
                $"Follow-up SLA reports are limited to {MaximumReportDays} days per request.");
        }

        var startUtc = ToUtcStart(reportFrom, offset);
        var endUtc = ToUtcStart(reportTo.AddDays(1), offset);
        var effectiveEndUtc = endUtc < nowUtc ? endUtc : nowUtc;
        var queryStartUtc = startUtc.AddDays(-(MaximumFollowUpLeadDays + 1));

        var tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Include(task => task.Activities.Where(activity =>
                activity.CreatedAtUtc >= queryStartUtc &&
                activity.CreatedAtUtc <= effectiveEndUtc))
            .Where(task => task.Activities.Any(activity =>
                activity.Action == WebsiteWorkAttentionActionService.FollowUpAssignedAction &&
                activity.CreatedAtUtc >= queryStartUtc &&
                activity.CreatedAtUtc <= effectiveEndUtc))
            .ToArrayAsync(cancellationToken);

        var lifecycles = tasks
            .SelectMany(task => BuildLifecycles(task.Activities, effectiveEndUtc))
            .Where(item =>
                item.DueAtUtc >= startUtc &&
                item.DueAtUtc < endUtc &&
                item.DueAtUtc <= effectiveEndUtc &&
                !item.InvalidatedBeforeDue)
            .ToArray();

        var ownerIds = lifecycles.Select(item => item.OwnerUserId).Distinct().ToArray();
        var ownerDirectory = ownerIds.Length == 0
            ? new Dictionary<Guid, OwnerDirectoryItem>()
            : (await dbContext.Users
                .AsNoTracking()
                .Include(user => user.Employee)
                    .ThenInclude(employee => employee!.Department)
                .Where(user => ownerIds.Contains(user.Id))
                .ToArrayAsync(cancellationToken))
                .ToDictionary(
                    user => user.Id,
                    user => new OwnerDirectoryItem(
                        user.Email,
                        user.Employee?.FullName,
                        user.Employee?.DepartmentId,
                        user.Employee?.Department?.Name));

        var summary = BuildMetrics(lifecycles, effectiveEndUtc);
        var managers = lifecycles
            .GroupBy(item => item.OwnerUserId)
            .Select(group =>
            {
                var latest = group.OrderByDescending(item => item.AssignedAtUtc).First();
                ownerDirectory.TryGetValue(group.Key, out var directory);
                var metrics = BuildMetrics(group.ToArray(), effectiveEndUtc);
                return new FollowUpSlaManagerResponse(
                    group.Key,
                    directory?.Email ?? latest.OwnerEmail ?? "Unknown account",
                    directory?.Name ?? latest.OwnerName,
                    directory?.DepartmentId,
                    directory?.DepartmentName,
                    metrics,
                    metrics.Escalated >= 2);
            })
            .OrderByDescending(item => item.Metrics.Escalated)
            .ThenByDescending(item => item.Metrics.SlaBreached)
            .ThenByDescending(item => item.Metrics.OpenOverdue)
            .ThenBy(item => item.OwnerName ?? item.OwnerEmail, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var departments = managers
            .GroupBy(item => new DepartmentKey(item.DepartmentId, item.DepartmentName ?? "No department"))
            .Select(group => new FollowUpSlaDepartmentResponse(
                group.Key.DepartmentId,
                group.Key.DepartmentName,
                group.Count(),
                group.Count(item => item.RepeatedEscalation),
                CombineMetrics(group.Select(item => item.Metrics).ToArray())))
            .OrderByDescending(item => item.Metrics.SlaBreached)
            .ThenByDescending(item => item.Metrics.Escalated)
            .ThenBy(item => item.DepartmentName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var periods = BuildPeriods(reportFrom, reportTo, grouping)
            .Select(period =>
            {
                var periodItems = lifecycles
                    .Where(item =>
                    {
                        var dueDate = DateOnly.FromDateTime(item.DueAtUtc + offset);
                        return dueDate >= period.From && dueDate <= period.To;
                    })
                    .ToArray();
                return new FollowUpSlaPeriodResponse(
                    period.From,
                    period.To,
                    BuildMetrics(periodItems, effectiveEndUtc));
            })
            .ToArray();

        return OperationResult<FollowUpSlaAnalyticsResponse>.Success(
            new FollowUpSlaAnalyticsResponse(
                nowUtc,
                reportFrom,
                reportTo,
                utcOffsetMinutes,
                grouping,
                _reminderOptions.EscalationAfterMinutes,
                managers.Count(item => item.RepeatedEscalation),
                summary,
                managers,
                departments,
                periods));
    }

    private static IReadOnlyCollection<FollowUpLifecycle> BuildLifecycles(
        IEnumerable<TaskActivity> activities,
        DateTime effectiveEndUtc)
    {
        var ordered = activities
            .Where(activity => activity.CreatedAtUtc <= effectiveEndUtc)
            .OrderBy(activity => activity.CreatedAtUtc)
            .ThenBy(activity => activity.Id)
            .ToArray();
        var result = new List<FollowUpLifecycle>();

        for (var index = 0; index < ordered.Length; index++)
        {
            var assignment = ordered[index];
            if (assignment.Action != WebsiteWorkAttentionActionService.FollowUpAssignedAction)
            {
                continue;
            }

            var details = ParseDetails(assignment.DetailsJson);
            var ownerUserId = ReadGuid(details, "followUpOwnerUserId");
            var dueAtUtc = ReadDateTime(details, "followUpDueAtUtc");
            if (!ownerUserId.HasValue || !dueAtUtc.HasValue)
            {
                continue;
            }

            TaskActivity? terminal = null;
            for (var terminalIndex = index + 1; terminalIndex < ordered.Length; terminalIndex++)
            {
                var candidate = ordered[terminalIndex];
                if (IsManagementAction(candidate.Action) || IsLifecycleAction(candidate.Action))
                {
                    terminal = candidate;
                    break;
                }
            }

            var resolvedAtUtc = terminal?.Action == WebsiteWorkAttentionActionService.FollowUpResolvedAction
                ? terminal.CreatedAtUtc
                : (DateTime?)null;
            var terminalAtUtc = terminal?.CreatedAtUtc;
            var invalidatedBeforeDue = terminal is not null &&
                                       resolvedAtUtc is null &&
                                       terminal.CreatedAtUtc < dueAtUtc.Value;
            var escalationCutoff = terminalAtUtc.HasValue && terminalAtUtc.Value < effectiveEndUtc
                ? terminalAtUtc.Value
                : effectiveEndUtc;
            var escalated = ordered.Any(activity =>
                activity.CreatedAtUtc >= assignment.CreatedAtUtc &&
                activity.CreatedAtUtc <= escalationCutoff &&
                IsEscalationNotification(activity, assignment.Id));

            result.Add(new FollowUpLifecycle(
                assignment.Id,
                ownerUserId.Value,
                ReadString(details, "followUpOwnerEmail"),
                ReadString(details, "followUpOwnerName"),
                assignment.CreatedAtUtc,
                dueAtUtc.Value,
                resolvedAtUtc,
                terminalAtUtc,
                invalidatedBeforeDue,
                escalated));
        }

        return result;
    }

    private static FollowUpSlaMetricsResponse BuildMetrics(
        IReadOnlyCollection<FollowUpLifecycle> items,
        DateTime effectiveEndUtc)
    {
        var resolved = items.Where(item => item.ResolvedAtUtc.HasValue).ToArray();
        var slaMet = items.Count(item =>
            item.ResolvedAtUtc.HasValue && item.ResolvedAtUtc.Value <= item.DueAtUtc);
        var breached = items.Count(item => IsBreached(item, effectiveEndUtc));
        var escalated = items.Count(item => item.Escalated);
        var openOverdue = items.Count(item =>
            !item.TerminalAtUtc.HasValue && item.DueAtUtc < effectiveEndUtc);

        var averageResolutionMinutes = AverageMinutes(resolved.Select(item =>
            (item.ResolvedAtUtc!.Value - item.AssignedAtUtc).TotalMinutes));
        var averageOverdueMinutes = AverageMinutes(resolved
            .Where(item => item.ResolvedAtUtc!.Value > item.DueAtUtc)
            .Select(item => (item.ResolvedAtUtc!.Value - item.DueAtUtc).TotalMinutes));
        var slaMetPercent = items.Count == 0
            ? 0m
            : Math.Round(100m * slaMet / items.Count, 2, MidpointRounding.AwayFromZero);

        return new FollowUpSlaMetricsResponse(
            items.Count,
            resolved.Length,
            slaMet,
            breached,
            escalated,
            openOverdue,
            averageResolutionMinutes,
            averageOverdueMinutes,
            slaMetPercent);
    }

    private static FollowUpSlaMetricsResponse CombineMetrics(
        IReadOnlyCollection<FollowUpSlaMetricsResponse> metrics)
    {
        var due = metrics.Sum(item => item.Due);
        var resolved = metrics.Sum(item => item.Resolved);
        var slaMet = metrics.Sum(item => item.SlaMet);
        var breached = metrics.Sum(item => item.SlaBreached);
        var escalated = metrics.Sum(item => item.Escalated);
        var openOverdue = metrics.Sum(item => item.OpenOverdue);

        var averageResolution = resolved == 0
            ? 0m
            : Math.Round(metrics.Sum(item => item.AverageResolutionMinutes * item.Resolved) / resolved, 1, MidpointRounding.AwayFromZero);
        var overdueResolved = metrics.Sum(item => Math.Max(0, item.Resolved - item.SlaMet));
        var averageOverdue = overdueResolved == 0
            ? 0m
            : Math.Round(metrics.Sum(item => item.AverageOverdueMinutes * Math.Max(0, item.Resolved - item.SlaMet)) / overdueResolved, 1, MidpointRounding.AwayFromZero);
        var slaMetPercent = due == 0
            ? 0m
            : Math.Round(100m * slaMet / due, 2, MidpointRounding.AwayFromZero);

        return new FollowUpSlaMetricsResponse(
            due,
            resolved,
            slaMet,
            breached,
            escalated,
            openOverdue,
            averageResolution,
            averageOverdue,
            slaMetPercent);
    }

    private static bool IsBreached(FollowUpLifecycle item, DateTime effectiveEndUtc)
    {
        if (item.ResolvedAtUtc.HasValue)
        {
            return item.ResolvedAtUtc.Value > item.DueAtUtc;
        }

        if (item.TerminalAtUtc.HasValue)
        {
            return item.TerminalAtUtc.Value > item.DueAtUtc;
        }

        return effectiveEndUtc > item.DueAtUtc;
    }

    private static decimal AverageMinutes(IEnumerable<double> values)
    {
        var array = values.Where(value => value >= 0).ToArray();
        return array.Length == 0
            ? 0m
            : Math.Round((decimal)array.Average(), 1, MidpointRounding.AwayFromZero);
    }

    private static bool IsEscalationNotification(TaskActivity activity, Guid sourceActivityId)
    {
        if (!AdminNotificationService.IsNotificationAction(activity.Action))
        {
            return false;
        }

        var details = ParseDetails(activity.DetailsJson);
        return string.Equals(
                   ReadString(details, "kind"),
                   AdminNotificationKind.FollowUpEscalated.ToString(),
                   StringComparison.OrdinalIgnoreCase) &&
               ReadGuid(details, "sourceActivityId") == sourceActivityId;
    }

    private static bool IsManagementAction(string action)
        => action == WebsiteWorkAttentionActionService.AcknowledgedAction ||
           action == WebsiteWorkAttentionActionService.SnoozedAction ||
           action == WebsiteWorkAttentionActionService.FollowUpAssignedAction ||
           action == WebsiteWorkAttentionActionService.FollowUpResolvedAction;

    private static bool IsLifecycleAction(string action)
        => action == WebsiteWorkService.ConfiguredAction ||
           action == WebsiteWorkService.StartedAction ||
           action == WebsiteWorkReviewService.SubmittedAction ||
           action == WebsiteWorkReviewService.ReopenedAction ||
           action == WebsiteWorkReviewService.ApprovedAction ||
           action == WebsiteWorkService.CompletedAction;

    private static JsonElement? ParseDetails(string detailsJson)
    {
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

    private static IReadOnlyCollection<(DateOnly From, DateOnly To)> BuildPeriods(
        DateOnly from,
        DateOnly to,
        FollowUpSlaGrouping grouping)
    {
        var periods = new List<(DateOnly From, DateOnly To)>();
        if (grouping == FollowUpSlaGrouping.Day)
        {
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                periods.Add((date, date));
            }
            return periods;
        }

        var dayOfWeek = (int)from.DayOfWeek;
        var daysSinceMonday = (dayOfWeek + 6) % 7;
        var calendarWeekStart = from.AddDays(-daysSinceMonday);
        for (var weekStart = calendarWeekStart; weekStart <= to; weekStart = weekStart.AddDays(7))
        {
            var weekEnd = weekStart.AddDays(6);
            var periodFrom = weekStart < from ? from : weekStart;
            var periodTo = weekEnd > to ? to : weekEnd;
            if (periodFrom <= periodTo)
            {
                periods.Add((periodFrom, periodTo));
            }
        }
        return periods;
    }

    private static DateTime ToUtcStart(DateOnly date, TimeSpan offset)
        => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), offset).UtcDateTime;

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record FollowUpLifecycle(
        Guid AssignmentActivityId,
        Guid OwnerUserId,
        string? OwnerEmail,
        string? OwnerName,
        DateTime AssignedAtUtc,
        DateTime DueAtUtc,
        DateTime? ResolvedAtUtc,
        DateTime? TerminalAtUtc,
        bool InvalidatedBeforeDue,
        bool Escalated);

    private sealed record OwnerDirectoryItem(
        string Email,
        string? Name,
        Guid? DepartmentId,
        string? DepartmentName);

    private sealed record DepartmentKey(Guid? DepartmentId, string DepartmentName);
}
