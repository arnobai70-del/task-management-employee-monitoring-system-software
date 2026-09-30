using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkTimelineService
{
    Task<OperationResult<WebsiteWorkEmployeeTimelineResponse>> GetEmployeeAsync(
        Guid employeeId,
        DateOnly? from,
        DateOnly? to,
        int utcOffsetMinutes,
        CancellationToken cancellationToken);
}

public sealed class WebsiteWorkTimelineService(
    AppDbContext dbContext,
    TimeProvider timeProvider) : IWebsiteWorkTimelineService
{
    private const int MaximumReportDays = 366;

    public async Task<OperationResult<WebsiteWorkEmployeeTimelineResponse>> GetEmployeeAsync(
        Guid employeeId,
        DateOnly? from,
        DateOnly? to,
        int utcOffsetMinutes,
        CancellationToken cancellationToken)
    {
        if (utcOffsetMinutes is < -840 or > 840)
        {
            return OperationResult<WebsiteWorkEmployeeTimelineResponse>.Invalid(
                "utc_offset_invalid",
                "UTC offset must be between -840 and 840 minutes.");
        }

        var nowUtc = UtcNow();
        var offset = TimeSpan.FromMinutes(utcOffsetMinutes);
        var localToday = DateOnly.FromDateTime(nowUtc + offset);
        var reportTo = to ?? localToday;
        var reportFrom = from ?? reportTo.AddDays(-6);

        if (reportFrom > reportTo)
        {
            return OperationResult<WebsiteWorkEmployeeTimelineResponse>.Invalid(
                "report_range_invalid",
                "The report start date cannot be after the end date.");
        }

        if (reportTo > localToday)
        {
            return OperationResult<WebsiteWorkEmployeeTimelineResponse>.Invalid(
                "report_range_future",
                "The productivity timeline cannot include future dates.");
        }

        var totalDays = reportTo.DayNumber - reportFrom.DayNumber + 1;
        if (totalDays > MaximumReportDays)
        {
            return OperationResult<WebsiteWorkEmployeeTimelineResponse>.Invalid(
                "report_range_too_large",
                $"Productivity timelines are limited to {MaximumReportDays} days per request.");
        }

        var employee = await dbContext.Employees
            .AsNoTracking()
            .Include(x => x.Department)
            .SingleOrDefaultAsync(x => x.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return OperationResult<WebsiteWorkEmployeeTimelineResponse>.NotFound(
                "employee_not_found",
                "Employee was not found.");
        }

        var startUtc = ToUtcStart(reportFrom, offset);
        var endUtc = ToUtcStart(reportTo.AddDays(1), offset);
        var effectiveEndUtc = endUtc < nowUtc ? endUtc : nowUtc;

        var tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Activities)
            .Where(x =>
                x.AssigneeEmployeeId == employeeId &&
                x.CreatedAtUtc < endUtc &&
                x.Activities.Any(activity => activity.Action == WebsiteWorkService.ConfiguredAction))
            .ToListAsync(cancellationToken);

        var relevantTasks = tasks
            .Where(task => HasActivityInPeriod(task, reportFrom, reportTo, startUtc, endUtc, effectiveEndUtc))
            .OrderByDescending(task => LastLifecycleAt(task))
            .ThenBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var actorIds = relevantTasks
            .SelectMany(task => task.Activities.Select(activity => activity.ActorUserId))
            .Append(relevantTasks.Select(task => task.CreatedByUserId).FirstOrDefault())
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        var actorEmails = actorIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.Users
                .AsNoTracking()
                .Where(user => actorIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken);

        var items = relevantTasks
            .Select(task => BuildItem(task, reportTo, startUtc, effectiveEndUtc, nowUtc, actorEmails))
            .ToArray();

        return OperationResult<WebsiteWorkEmployeeTimelineResponse>.Success(
            new WebsiteWorkEmployeeTimelineResponse(
                nowUtc,
                reportFrom,
                reportTo,
                utcOffsetMinutes,
                employee.Id,
                employee.EmployeeCode,
                employee.FullName,
                employee.DepartmentId,
                employee.Department?.Name,
                items));
    }

    private static WebsiteWorkTimelineItemResponse BuildItem(
        ProjectTask task,
        DateOnly reportTo,
        DateTime reportStartUtc,
        DateTime reportEndUtc,
        DateTime nowUtc,
        IReadOnlyDictionary<Guid, string> actorEmails)
    {
        var ordered = task.Activities.OrderBy(x => x.CreatedAtUtc).ToArray();
        var hasApprovedLifecycle = ordered.Any(x => x.Action == WebsiteWorkReviewService.ApprovedAction);
        var events = new List<WebsiteWorkTimelineEventResponse>
        {
            new(
                WebsiteWorkTimelineEventType.Assigned,
                task.CreatedAtUtc,
                "Assigned",
                null,
                ActorEmail(task.CreatedByUserId, actorEmails))
        };

        foreach (var activity in ordered)
        {
            WebsiteWorkTimelineEventResponse? timelineEvent = activity.Action switch
            {
                WebsiteWorkService.StartedAction => new(
                    WebsiteWorkTimelineEventType.Started,
                    activity.CreatedAtUtc,
                    "Started work",
                    null,
                    ActorEmail(activity.ActorUserId, actorEmails)),
                WebsiteWorkReviewService.SubmittedAction => new(
                    WebsiteWorkTimelineEventType.Submitted,
                    activity.CreatedAtUtc,
                    "Submitted for review",
                    null,
                    ActorEmail(activity.ActorUserId, actorEmails)),
                WebsiteWorkReviewService.ReopenedAction => new(
                    WebsiteWorkTimelineEventType.CorrectionRequested,
                    activity.CreatedAtUtc,
                    "Correction requested",
                    ReadComment(activity.DetailsJson),
                    ActorEmail(activity.ActorUserId, actorEmails)),
                WebsiteWorkReviewService.ApprovedAction => new(
                    WebsiteWorkTimelineEventType.Approved,
                    activity.CreatedAtUtc,
                    "Approved",
                    ReadComment(activity.DetailsJson),
                    ActorEmail(activity.ActorUserId, actorEmails)),
                WebsiteWorkService.CompletedAction when !hasApprovedLifecycle => new(
                    WebsiteWorkTimelineEventType.Completed,
                    activity.CreatedAtUtc,
                    "Completed",
                    null,
                    ActorEmail(activity.ActorUserId, actorEmails)),
                _ => null
            };

            if (timelineEvent is not null)
            {
                events.Add(timelineEvent);
            }
        }

        events.Sort((left, right) => left.AtUtc.CompareTo(right.AtUtc));

        var firstStartedAtUtc = ordered
            .Where(x => x.Action == WebsiteWorkService.StartedAction)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .FirstOrDefault();
        var lastSubmittedAtUtc = ordered
            .Where(x => x.Action == WebsiteWorkReviewService.SubmittedAction)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .LastOrDefault();
        var approvedAtUtc = ordered
            .Where(x => x.Action == WebsiteWorkReviewService.ApprovedAction)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .LastOrDefault()
            ?? ordered
                .Where(x => x.Action == WebsiteWorkService.CompletedAction)
                .Select(x => (DateTime?)x.CreatedAtUtc)
                .LastOrDefault();

        var finalAtReportEnd = ordered.Any(activity =>
            activity.Action == WebsiteWorkService.CompletedAction &&
            activity.CreatedAtUtc < reportEndUtc);
        var overdueAtPeriodEnd = task.Status != ProjectTaskStatus.Cancelled &&
            task.DueDate.HasValue &&
            task.DueDate.Value < reportTo &&
            !finalAtReportEnd;

        return new WebsiteWorkTimelineItemResponse(
            task.Id,
            task.ProjectId,
            task.Project.Code,
            task.Project.Name,
            task.Title,
            task.Status,
            task.DueDate,
            task.CreatedAtUtc,
            firstStartedAtUtc,
            lastSubmittedAtUtc,
            approvedAtUtc,
            ordered.Count(x => x.Action == WebsiteWorkReviewService.ReopenedAction),
            CalculateWorkingSeconds(ordered, reportStartUtc, reportEndUtc),
            CalculateWorkingSeconds(ordered, task.CreatedAtUtc, nowUtc),
            overdueAtPeriodEnd,
            events);
    }

    private static bool HasActivityInPeriod(
        ProjectTask task,
        DateOnly reportFrom,
        DateOnly reportTo,
        DateTime startUtc,
        DateTime endUtc,
        DateTime effectiveEndUtc)
    {
        if (task.CreatedAtUtc >= startUtc && task.CreatedAtUtc < endUtc)
        {
            return true;
        }

        if (task.Activities.Any(activity =>
            IsLifecycleAction(activity.Action) &&
            activity.CreatedAtUtc >= startUtc &&
            activity.CreatedAtUtc < endUtc))
        {
            return true;
        }

        if (CalculateWorkingSeconds(task.Activities, startUtc, effectiveEndUtc) > 0)
        {
            return true;
        }

        return task.Status != ProjectTaskStatus.Cancelled &&
            task.DueDate.HasValue &&
            task.DueDate.Value < reportTo &&
            !task.Activities.Any(activity =>
                activity.Action == WebsiteWorkService.CompletedAction &&
                activity.CreatedAtUtc < endUtc);
    }

    private static bool IsLifecycleAction(string action)
        => action == WebsiteWorkService.StartedAction ||
           action == WebsiteWorkReviewService.SubmittedAction ||
           action == WebsiteWorkReviewService.ReopenedAction ||
           action == WebsiteWorkReviewService.ApprovedAction ||
           action == WebsiteWorkService.CompletedAction;

    private static long CalculateWorkingSeconds(
        IEnumerable<TaskActivity> activities,
        DateTime reportStartUtc,
        DateTime reportEndUtc)
    {
        if (reportEndUtc <= reportStartUtc)
        {
            return 0;
        }

        DateTime? openAtUtc = null;
        long seconds = 0;
        foreach (var activity in activities.OrderBy(x => x.CreatedAtUtc))
        {
            if (activity.Action == WebsiteWorkService.StartedAction ||
                activity.Action == WebsiteWorkReviewService.ReopenedAction)
            {
                openAtUtc ??= activity.CreatedAtUtc;
                continue;
            }

            if (activity.Action != WebsiteWorkReviewService.SubmittedAction &&
                activity.Action != WebsiteWorkService.CompletedAction)
            {
                continue;
            }

            if (openAtUtc.HasValue)
            {
                seconds += OverlapSeconds(openAtUtc.Value, activity.CreatedAtUtc, reportStartUtc, reportEndUtc);
                openAtUtc = null;
            }
        }

        if (openAtUtc.HasValue)
        {
            seconds += OverlapSeconds(openAtUtc.Value, reportEndUtc, reportStartUtc, reportEndUtc);
        }

        return seconds;
    }

    private static long OverlapSeconds(
        DateTime workStartUtc,
        DateTime workEndUtc,
        DateTime reportStartUtc,
        DateTime reportEndUtc)
    {
        var start = workStartUtc > reportStartUtc ? workStartUtc : reportStartUtc;
        var end = workEndUtc < reportEndUtc ? workEndUtc : reportEndUtc;
        return end <= start ? 0 : Math.Max(0L, (long)(end - start).TotalSeconds);
    }

    private static DateTime LastLifecycleAt(ProjectTask task)
        => task.Activities
            .Where(activity => IsLifecycleAction(activity.Action))
            .Select(activity => activity.CreatedAtUtc)
            .DefaultIfEmpty(task.CreatedAtUtc)
            .Max();

    private static string? ReadComment(string detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            if (!document.RootElement.TryGetProperty("comment", out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var comment = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(comment) ? null : comment;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ActorEmail(Guid? actorUserId, IReadOnlyDictionary<Guid, string> actorEmails)
        => actorUserId.HasValue && actorEmails.TryGetValue(actorUserId.Value, out var email) ? email : null;

    private static DateTime ToUtcStart(DateOnly date, TimeSpan offset)
        => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), offset).UtcDateTime;

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
}
