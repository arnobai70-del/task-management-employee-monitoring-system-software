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

        var completionActivities = await dbContext.TaskActivities
            .AsNoTracking()
            .Include(x => x.ProjectTask)
                .ThenInclude(x => x.AssigneeEmployee)
            .Where(x =>
                x.Action == WebsiteWorkService.CompletedAction &&
                x.CreatedAtUtc >= startUtc &&
                x.CreatedAtUtc < endUtc)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

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
            .Concat(completionActivities
                .Where(x => x.ProjectTask.AssigneeEmployeeId.HasValue)
                .Select(x => x.ProjectTask.AssigneeEmployeeId!.Value))
            .Distinct()
            .ToArray();

        var employees = new List<WebsiteWorkEmployeeTodayResponse>(employeeIds.Length);
        foreach (var employeeId in employeeIds)
        {
            var activeTask = activeTasks.FirstOrDefault(x => x.AssigneeEmployeeId == employeeId);
            var completion = completionActivities.FirstOrDefault(x => x.ProjectTask.AssigneeEmployeeId == employeeId);
            var employee = activeTask?.AssigneeEmployee ?? completion?.ProjectTask.AssigneeEmployee;
            if (employee is null)
            {
                continue;
            }

            var employeeCompletions = completionActivities
                .Where(x => x.ProjectTask.AssigneeEmployeeId == employeeId)
                .ToArray();

            employees.Add(new WebsiteWorkEmployeeTodayResponse(
                employeeId,
                employee.EmployeeCode,
                employee.FullName,
                activeTasks.Count(x => x.AssigneeEmployeeId == employeeId),
                employeeCompletions.Length,
                employeeCompletions.Length == 0 ? null : employeeCompletions.Max(x => x.CreatedAtUtc)));
        }

        return new WebsiteWorkProgressResponse(
            now,
            utcOffsetMinutes,
            active.Length,
            completionActivities.Count,
            active,
            employees
                .OrderByDescending(x => x.CompletedToday)
                .ThenByDescending(x => x.WorkingNow)
                .ThenBy(x => x.EmployeeName)
                .ToArray());
    }
}
