using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkProgressService
{
    Task<WebsiteWorkProgressResponse> GetAsync(
        int utcOffsetMinutes,
        CancellationToken cancellationToken);
}

public sealed class WebsiteWorkProgressService(
    AppDbContext dbContext,
    TimeProvider timeProvider) : IWebsiteWorkProgressService
{
    public async Task<WebsiteWorkProgressResponse> GetAsync(
        int utcOffsetMinutes,
        CancellationToken cancellationToken)
    {
        utcOffsetMinutes = Math.Clamp(utcOffsetMinutes, -840, 840);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var offset = TimeSpan.FromMinutes(utcOffsetMinutes);
        var localNow = now + offset;
        var localToday = DateOnly.FromDateTime(localNow);
        var startUtc = DateTime.SpecifyKind(localNow.Date - offset, DateTimeKind.Utc);
        var endUtc = startUtc.AddDays(1);

        var activeTasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.AssigneeEmployee)
            .Include(x => x.Activities)
            .Where(x =>
                x.Status == ProjectTaskStatus.InProgress &&
                x.AssigneeEmployeeId != null &&
                x.Activities.Any(activity => activity.Action == WebsiteWorkService.ConfiguredAction))
            .OrderBy(x => x.AssigneeEmployee!.NormalizedFullName)
            .ThenBy(x => x.NormalizedTitle)
            .ToListAsync(cancellationToken);

        var pendingCandidates = await dbContext.ProjectTasks
            .AsNoTracking()
            .Include(x => x.AssigneeEmployee)
            .Include(x => x.Activities)
            .Where(x =>
                x.Status == ProjectTaskStatus.Blocked &&
                x.AssigneeEmployeeId != null &&
                x.Activities.Any(activity => activity.Action == WebsiteWorkService.ConfiguredAction))
            .ToListAsync(cancellationToken);
        var pendingTasks = pendingCandidates
            .Where(x => WebsiteWorkReviewService.ResolveState(x.Activities) == WebsiteWorkReviewStates.PendingReview)
            .ToArray();

        var activityToday = await dbContext.TaskActivities
            .AsNoTracking()
            .Include(x => x.ProjectTask)
                .ThenInclude(x => x.AssigneeEmployee)
            .Where(x =>
                (x.Action == WebsiteWorkService.CompletedAction ||
                 x.Action == WebsiteWorkReviewService.ApprovedAction ||
                 x.Action == WebsiteWorkReviewService.ReopenedAction) &&
                x.CreatedAtUtc >= startUtc &&
                x.CreatedAtUtc < endUtc)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var submittedToday = activityToday.Where(x => x.Action == WebsiteWorkService.CompletedAction).ToArray();
        var approvedToday = activityToday.Where(x => x.Action == WebsiteWorkReviewService.ApprovedAction).ToArray();
        var reopenedToday = activityToday.Where(x => x.Action == WebsiteWorkReviewService.ReopenedAction).ToArray();

        var active = activeTasks
            .Select(task =>
            {
                var startedAt = task.Activities
                    .Where(x => x.Action == WebsiteWorkService.StartedAction || x.Action == WebsiteWorkReviewService.ReopenedAction)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Select(x => (DateTime?)x.CreatedAtUtc)
                    .FirstOrDefault() ?? task.UpdatedAtUtc;
                var elapsedSeconds = Math.Max(0L, (long)(now - startedAt).TotalSeconds);
                return new WebsiteWorkActiveProgressResponse(
                    task.Id,
                    task.ProjectId,
                    task.Project.Name,
                    task.AssigneeEmployeeId!.Value,
                    task.AssigneeEmployee?.EmployeeCode ?? string.Empty,
                    task.AssigneeEmployee?.FullName ?? "Unknown employee",
                    task.Title,
                    startedAt,
                    elapsedSeconds,
                    task.DueDate,
                    task.DueDate.HasValue && task.DueDate.Value < localToday);
            })
            .OrderByDescending(x => x.ElapsedSeconds)
            .ThenBy(x => x.EmployeeName)
            .ToArray();

        var employeeIds = activeTasks
            .Where(x => x.AssigneeEmployeeId.HasValue)
            .Select(x => x.AssigneeEmployeeId!.Value)
            .Concat(pendingTasks.Select(x => x.AssigneeEmployeeId!.Value))
            .Concat(activityToday
                .Where(x => x.ProjectTask.AssigneeEmployeeId.HasValue)
                .Select(x => x.ProjectTask.AssigneeEmployeeId!.Value))
            .Distinct()
            .ToArray();

        var employees = new List<WebsiteWorkEmployeeTodayResponse>(employeeIds.Length);
        foreach (var employeeId in employeeIds)
        {
            var activeTask = activeTasks.FirstOrDefault(x => x.AssigneeEmployeeId == employeeId);
            var pendingTask = pendingTasks.FirstOrDefault(x => x.AssigneeEmployeeId == employeeId);
            var activity = activityToday.FirstOrDefault(x => x.ProjectTask.AssigneeEmployeeId == employeeId);
            var employee = activeTask?.AssigneeEmployee ?? pendingTask?.AssigneeEmployee ?? activity?.ProjectTask.AssigneeEmployee;
            if (employee is null)
            {
                continue;
            }

            var employeeSubmitted = submittedToday.Where(x => x.ProjectTask.AssigneeEmployeeId == employeeId).ToArray();
            var employeeApproved = approvedToday.Where(x => x.ProjectTask.AssigneeEmployeeId == employeeId).ToArray();
            var employeeReopened = reopenedToday.Where(x => x.ProjectTask.AssigneeEmployeeId == employeeId).ToArray();

            employees.Add(new WebsiteWorkEmployeeTodayResponse(
                employeeId,
                employee.EmployeeCode,
                employee.FullName,
                activeTasks.Count(x => x.AssigneeEmployeeId == employeeId),
                pendingTasks.Count(x => x.AssigneeEmployeeId == employeeId),
                employeeSubmitted.Length,
                employeeApproved.Length,
                employeeReopened.Length,
                employeeSubmitted.Length == 0 ? null : employeeSubmitted.Max(x => x.CreatedAtUtc),
                employeeApproved.Length == 0 ? null : employeeApproved.Max(x => x.CreatedAtUtc)));
        }

        return new WebsiteWorkProgressResponse(
            now,
            utcOffsetMinutes,
            active.Length,
            pendingTasks.Length,
            submittedToday.Length,
            approvedToday.Length,
            reopenedToday.Length,
            active,
            employees
                .OrderByDescending(x => x.ApprovedToday)
                .ThenByDescending(x => x.SubmittedToday)
                .ThenByDescending(x => x.PendingReview)
                .ThenByDescending(x => x.WorkingNow)
                .ThenBy(x => x.EmployeeName)
                .ToArray());
    }
}
