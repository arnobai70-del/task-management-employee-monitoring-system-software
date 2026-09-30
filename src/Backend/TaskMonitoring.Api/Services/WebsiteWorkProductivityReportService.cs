using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkProductivityReportService
{
    Task<OperationResult<WebsiteWorkProductivityReportResponse>> GetAsync(
        DateOnly? from,
        DateOnly? to,
        int utcOffsetMinutes,
        WebsiteWorkProductivityGrouping grouping,
        CancellationToken cancellationToken);
}

public sealed class WebsiteWorkProductivityReportService(
    AppDbContext dbContext,
    TimeProvider timeProvider) : IWebsiteWorkProductivityReportService
{
    private const int MaximumReportDays = 366;

    public async Task<OperationResult<WebsiteWorkProductivityReportResponse>> GetAsync(
        DateOnly? from,
        DateOnly? to,
        int utcOffsetMinutes,
        WebsiteWorkProductivityGrouping grouping,
        CancellationToken cancellationToken)
    {
        if (utcOffsetMinutes is < -840 or > 840)
        {
            return OperationResult<WebsiteWorkProductivityReportResponse>.Invalid(
                "utc_offset_invalid",
                "UTC offset must be between -840 and 840 minutes.");
        }

        if (!Enum.IsDefined(grouping))
        {
            return OperationResult<WebsiteWorkProductivityReportResponse>.Invalid(
                "productivity_grouping_invalid",
                "Productivity grouping must be Day or Week.");
        }

        var nowUtc = UtcNow();
        var offset = TimeSpan.FromMinutes(utcOffsetMinutes);
        var localToday = DateOnly.FromDateTime(nowUtc + offset);
        var reportTo = to ?? localToday;
        var reportFrom = from ?? reportTo.AddDays(-6);

        if (reportFrom > reportTo)
        {
            return OperationResult<WebsiteWorkProductivityReportResponse>.Invalid(
                "report_range_invalid",
                "The report start date cannot be after the end date.");
        }

        if (reportTo > localToday)
        {
            return OperationResult<WebsiteWorkProductivityReportResponse>.Invalid(
                "report_range_future",
                "The productivity report cannot include future dates.");
        }

        var totalDays = reportTo.DayNumber - reportFrom.DayNumber + 1;
        if (totalDays > MaximumReportDays)
        {
            return OperationResult<WebsiteWorkProductivityReportResponse>.Invalid(
                "report_range_too_large",
                $"Productivity reports are limited to {MaximumReportDays} days per request.");
        }

        var startUtc = ToUtcStart(reportFrom, offset);
        var endUtc = ToUtcStart(reportTo.AddDays(1), offset);

        var tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Include(x => x.AssigneeEmployee)
                .ThenInclude(x => x!.Department)
            .Include(x => x.Activities)
            .Where(x =>
                x.AssigneeEmployeeId != null &&
                x.CreatedAtUtc < endUtc &&
                x.Activities.Any(activity => activity.Action == WebsiteWorkService.ConfiguredAction) &&
                (
                    x.CreatedAtUtc >= startUtc ||
                    x.UpdatedAtUtc >= startUtc ||
                    (x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled)))
            .ToListAsync(cancellationToken);

        var summary = BuildMetrics(tasks, reportFrom, reportTo, offset, nowUtc);

        var employees = tasks
            .Where(x => x.AssigneeEmployeeId.HasValue && x.AssigneeEmployee is not null)
            .GroupBy(x => x.AssigneeEmployeeId!.Value)
            .Select(group =>
            {
                var employee = group.First().AssigneeEmployee!;
                var metrics = BuildMetrics(group.ToArray(), reportFrom, reportTo, offset, nowUtc);
                return new WebsiteWorkEmployeeProductivityResponse(
                    employee.Id,
                    employee.EmployeeCode,
                    employee.FullName,
                    employee.DepartmentId,
                    employee.Department?.Name,
                    metrics);
            })
            .Where(x => HasReportActivity(x.Metrics))
            .OrderByDescending(x => x.Metrics.Approved)
            .ThenByDescending(x => x.Metrics.WorkingSeconds)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var periods = BuildPeriods(reportFrom, reportTo, grouping)
            .Select(period => new WebsiteWorkProductivityPeriodResponse(
                period.From,
                period.To,
                BuildMetrics(tasks, period.From, period.To, offset, nowUtc)))
            .ToArray();

        return OperationResult<WebsiteWorkProductivityReportResponse>.Success(
            new WebsiteWorkProductivityReportResponse(
                nowUtc,
                reportFrom,
                reportTo,
                utcOffsetMinutes,
                grouping,
                summary,
                employees,
                periods));
    }

    private static WebsiteWorkProductivityMetricsResponse BuildMetrics(
        IReadOnlyCollection<ProjectTask> tasks,
        DateOnly from,
        DateOnly to,
        TimeSpan offset,
        DateTime nowUtc)
    {
        var startUtc = ToUtcStart(from, offset);
        var endUtc = ToUtcStart(to.AddDays(1), offset);
        var effectiveEndUtc = endUtc < nowUtc ? endUtc : nowUtc;

        var assignedTasks = tasks
            .Where(task => task.CreatedAtUtc >= startUtc && task.CreatedAtUtc < endUtc)
            .ToArray();

        var started = tasks.Count(task => task.Activities.Any(activity =>
            activity.Action == WebsiteWorkService.StartedAction &&
            InRange(activity.CreatedAtUtc, startUtc, endUtc)));

        var workingSeconds = tasks.Sum(task => CalculateWorkingSeconds(task.Activities, startUtc, effectiveEndUtc));

        var submitted = tasks.Sum(task => task.Activities.Count(activity =>
            activity.Action == WebsiteWorkReviewService.SubmittedAction &&
            InRange(activity.CreatedAtUtc, startUtc, endUtc)));

        var approved = tasks.Sum(task => task.Activities.Count(activity =>
            activity.Action == WebsiteWorkService.CompletedAction &&
            InRange(activity.CreatedAtUtc, startUtc, endUtc)));

        var reopened = tasks.Sum(task => task.Activities.Count(activity =>
            activity.Action == WebsiteWorkReviewService.ReopenedAction &&
            InRange(activity.CreatedAtUtc, startUtc, endUtc)));

        var overdue = tasks.Count(task =>
            task.Status != ProjectTaskStatus.Cancelled &&
            task.CreatedAtUtc < endUtc &&
            task.DueDate.HasValue &&
            task.DueDate.Value < to &&
            !WasFinalBy(task, endUtc));

        var assignedApproved = assignedTasks.Count(task => WasFinalBy(task, endUtc));
        var completionPercent = assignedTasks.Length == 0
            ? 0m
            : Math.Round(100m * assignedApproved / assignedTasks.Length, 2, MidpointRounding.AwayFromZero);

        return new WebsiteWorkProductivityMetricsResponse(
            assignedTasks.Length,
            started,
            workingSeconds,
            submitted,
            approved,
            reopened,
            overdue,
            completionPercent);
    }

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

    private static bool WasFinalBy(ProjectTask task, DateTime endUtc)
        => task.Activities.Any(activity =>
            activity.Action == WebsiteWorkService.CompletedAction &&
            activity.CreatedAtUtc < endUtc);

    private static bool InRange(DateTime value, DateTime startUtc, DateTime endUtc)
        => value >= startUtc && value < endUtc;

    private static bool HasReportActivity(WebsiteWorkProductivityMetricsResponse metrics)
        => metrics.Assigned > 0 ||
           metrics.Started > 0 ||
           metrics.WorkingSeconds > 0 ||
           metrics.Submitted > 0 ||
           metrics.Approved > 0 ||
           metrics.Reopened > 0 ||
           metrics.Overdue > 0;

    private static IReadOnlyCollection<(DateOnly From, DateOnly To)> BuildPeriods(
        DateOnly from,
        DateOnly to,
        WebsiteWorkProductivityGrouping grouping)
    {
        var periods = new List<(DateOnly From, DateOnly To)>();
        if (grouping == WebsiteWorkProductivityGrouping.Day)
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
}
