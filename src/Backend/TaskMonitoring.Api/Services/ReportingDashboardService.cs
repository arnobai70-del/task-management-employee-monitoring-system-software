using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IReportingDashboardService
{
    Task<OperationResult<DashboardOverviewResponse>> GetDashboardAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyCollection<AttendanceDailyMetricResponse>>> GetAttendanceDailyAsync(DateOnly? from, DateOnly? to, Guid? departmentId, CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyCollection<ProjectProgressResponse>>> GetProjectProgressAsync(Guid? projectId, CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyCollection<EmployeeWorkloadResponse>>> GetEmployeeWorkloadAsync(Guid? departmentId, int limit, CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyCollection<SurveyProgressResponse>>> GetSurveyProgressAsync(Guid? projectId, CancellationToken cancellationToken);
}

public sealed class ReportingDashboardService(AppDbContext dbContext, TimeProvider timeProvider) : IReportingDashboardService
{
    private const int MaximumReportDays = 366;

    public async Task<OperationResult<DashboardOverviewResponse>> GetDashboardAsync(
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        var range = ResolveRange(from, to);
        if (range.Error is not null)
        {
            return OperationResult<DashboardOverviewResponse>.Invalid(range.Error.Code, range.Error.Message);
        }

        var today = Today();
        var startUtc = ToUtc(range.From, TimeOnly.MinValue);
        var endUtc = ToUtc(range.To, TimeOnly.MaxValue);

        var activeEmployees = await dbContext.Employees.AsNoTracking().CountAsync(x => x.IsActive, cancellationToken);
        var activeDepartments = await dbContext.Departments.AsNoTracking().CountAsync(x => x.IsActive, cancellationToken);

        var attendanceQuery = dbContext.WorkSessions.AsNoTracking()
            .Where(x => x.WorkDate >= range.From && x.WorkDate <= range.To);
        var attendanceSessions = await attendanceQuery.CountAsync(cancellationToken);
        var attendanceEmployees = await attendanceQuery.Select(x => x.EmployeeId).Distinct().CountAsync(cancellationToken);
        var completedSessions = await attendanceQuery.CountAsync(x => x.EndedAtUtc != null, cancellationToken);
        var openSessions = await attendanceQuery.CountAsync(x => x.EndedAtUtc == null, cancellationToken);
        var lateSessions = await attendanceQuery.CountAsync(x => x.LateMinutes > 0, cancellationToken);
        var earlyLeaveSessions = await attendanceQuery.CountAsync(x => x.EarlyLeaveMinutes != null && x.EarlyLeaveMinutes > 0, cancellationToken);
        var totalBreakMinutes = await attendanceQuery.Select(x => (int?)x.TotalBreakMinutes).SumAsync(cancellationToken) ?? 0;

        var projects = dbContext.Projects.AsNoTracking();
        var planningProjects = await projects.CountAsync(x => x.Status == ProjectStatus.Planning, cancellationToken);
        var activeProjects = await projects.CountAsync(x => x.Status == ProjectStatus.Active, cancellationToken);
        var onHoldProjects = await projects.CountAsync(x => x.Status == ProjectStatus.OnHold, cancellationToken);
        var completedProjects = await projects.CountAsync(x => x.Status == ProjectStatus.Completed, cancellationToken);
        var archivedProjects = await projects.CountAsync(x => x.Status == ProjectStatus.Archived, cancellationToken);
        var overdueProjects = await projects.CountAsync(x =>
            x.DueDate != null && x.DueDate < today && x.Status != ProjectStatus.Completed && x.Status != ProjectStatus.Archived,
            cancellationToken);

        var tasks = dbContext.ProjectTasks.AsNoTracking();
        var openTasks = await tasks.CountAsync(x => x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled, cancellationToken);
        var toDoTasks = await tasks.CountAsync(x => x.Status == ProjectTaskStatus.ToDo, cancellationToken);
        var inProgressTasks = await tasks.CountAsync(x => x.Status == ProjectTaskStatus.InProgress, cancellationToken);
        var blockedTasks = await tasks.CountAsync(x => x.Status == ProjectTaskStatus.Blocked, cancellationToken);
        var doneTasks = await tasks.CountAsync(x => x.Status == ProjectTaskStatus.Done, cancellationToken);
        var cancelledTasks = await tasks.CountAsync(x => x.Status == ProjectTaskStatus.Cancelled, cancellationToken);
        var overdueTasks = await tasks.CountAsync(x =>
            x.DueDate != null && x.DueDate < today && x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled,
            cancellationToken);
        var unassignedOpenTasks = await tasks.CountAsync(x =>
            x.AssigneeEmployeeId == null && x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled,
            cancellationToken);
        var completedInPeriod = await tasks.CountAsync(x =>
            x.CompletedAtUtc != null && x.CompletedAtUtc >= startUtc && x.CompletedAtUtc <= endUtc,
            cancellationToken);

        var assignments = dbContext.SurveyAssignments.AsNoTracking();
        var publishedForms = await dbContext.SurveyForms.AsNoTracking().CountAsync(x => x.Status == SurveyFormStatus.Published, cancellationToken);
        var activeAssignments = await assignments.CountAsync(x =>
            x.Status != SurveyAssignmentStatus.Approved && x.Status != SurveyAssignmentStatus.Cancelled,
            cancellationToken);
        var pendingReview = await assignments.CountAsync(x => x.Status == SurveyAssignmentStatus.Submitted, cancellationToken);
        var approvedAssignments = await assignments.CountAsync(x => x.Status == SurveyAssignmentStatus.Approved, cancellationToken);
        var rejectedAssignments = await assignments.CountAsync(x => x.Status == SurveyAssignmentStatus.Rejected, cancellationToken);
        var overdueAssignments = await assignments.CountAsync(x =>
            x.DueDate != null && x.DueDate < today && x.Status != SurveyAssignmentStatus.Approved && x.Status != SurveyAssignmentStatus.Cancelled,
            cancellationToken);
        var submittedInPeriod = await dbContext.SurveySubmissions.AsNoTracking().CountAsync(x =>
            x.SubmittedAtUtc != null && x.SubmittedAtUtc >= startUtc && x.SubmittedAtUtc <= endUtc,
            cancellationToken);
        var reviewedInPeriod = await dbContext.SurveySubmissions.AsNoTracking().CountAsync(x =>
            x.ReviewedAtUtc != null && x.ReviewedAtUtc >= startUtc && x.ReviewedAtUtc <= endUtc,
            cancellationToken);

        return OperationResult<DashboardOverviewResponse>.Success(new DashboardOverviewResponse(
            UtcNow(),
            range.From,
            range.To,
            new WorkforceDashboardMetrics(activeEmployees, activeDepartments),
            new AttendanceDashboardMetrics(
                attendanceSessions,
                attendanceEmployees,
                completedSessions,
                openSessions,
                lateSessions,
                earlyLeaveSessions,
                totalBreakMinutes),
            new ProjectDashboardMetrics(
                planningProjects,
                activeProjects,
                onHoldProjects,
                completedProjects,
                archivedProjects,
                overdueProjects),
            new TaskDashboardMetrics(
                openTasks,
                toDoTasks,
                inProgressTasks,
                blockedTasks,
                doneTasks,
                cancelledTasks,
                overdueTasks,
                unassignedOpenTasks,
                completedInPeriod),
            new SurveyDashboardMetrics(
                publishedForms,
                activeAssignments,
                pendingReview,
                approvedAssignments,
                rejectedAssignments,
                overdueAssignments,
                submittedInPeriod,
                reviewedInPeriod)));
    }

    public async Task<OperationResult<IReadOnlyCollection<AttendanceDailyMetricResponse>>> GetAttendanceDailyAsync(
        DateOnly? from,
        DateOnly? to,
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        var range = ResolveRange(from, to);
        if (range.Error is not null)
        {
            return OperationResult<IReadOnlyCollection<AttendanceDailyMetricResponse>>.Invalid(range.Error.Code, range.Error.Message);
        }

        if (departmentId.HasValue && !await dbContext.Departments.AsNoTracking().AnyAsync(x => x.Id == departmentId.Value, cancellationToken))
        {
            return OperationResult<IReadOnlyCollection<AttendanceDailyMetricResponse>>.NotFound("department_not_found", "Department was not found.");
        }

        var query = dbContext.WorkSessions.AsNoTracking()
            .Where(x => x.WorkDate >= range.From && x.WorkDate <= range.To);
        if (departmentId.HasValue)
        {
            query = query.Where(x => x.Employee.DepartmentId == departmentId.Value);
        }

        var rows = await query
            .Select(x => new
            {
                x.WorkDate,
                x.EmployeeId,
                x.EndedAtUtc,
                x.LateMinutes,
                x.EarlyLeaveMinutes,
                x.TotalBreakMinutes
            })
            .ToListAsync(cancellationToken);

        var groups = rows.GroupBy(x => x.WorkDate).ToDictionary(x => x.Key);
        var result = new List<AttendanceDailyMetricResponse>();
        for (var date = range.From; date <= range.To; date = date.AddDays(1))
        {
            if (!groups.TryGetValue(date, out var dayRows))
            {
                result.Add(new AttendanceDailyMetricResponse(date, 0, 0, 0, 0, 0, 0, 0));
                continue;
            }

            var materialized = dayRows.ToArray();
            result.Add(new AttendanceDailyMetricResponse(
                date,
                materialized.Length,
                materialized.Select(x => x.EmployeeId).Distinct().Count(),
                materialized.Count(x => x.EndedAtUtc is not null),
                materialized.Count(x => x.EndedAtUtc is null),
                materialized.Count(x => x.LateMinutes > 0),
                materialized.Count(x => x.EarlyLeaveMinutes is > 0),
                materialized.Sum(x => x.TotalBreakMinutes)));
        }

        return OperationResult<IReadOnlyCollection<AttendanceDailyMetricResponse>>.Success(result);
    }

    public async Task<OperationResult<IReadOnlyCollection<ProjectProgressResponse>>> GetProjectProgressAsync(
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (projectId.HasValue && !await dbContext.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId.Value, cancellationToken))
        {
            return OperationResult<IReadOnlyCollection<ProjectProgressResponse>>.NotFound("project_not_found", "Project was not found.");
        }

        var today = Today();
        var query = dbContext.Projects.AsNoTracking();
        if (projectId.HasValue)
        {
            query = query.Where(x => x.Id == projectId.Value);
        }

        var rows = await query
            .OrderBy(x => x.Status)
            .ThenBy(x => x.NormalizedName)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.Name,
                x.Status,
                x.DueDate,
                ActiveMembers = x.Members.Count(member => member.IsActive),
                TotalTasks = x.Tasks.Count,
                OpenTasks = x.Tasks.Count(task => task.Status != ProjectTaskStatus.Done && task.Status != ProjectTaskStatus.Cancelled),
                DoneTasks = x.Tasks.Count(task => task.Status == ProjectTaskStatus.Done),
                BlockedTasks = x.Tasks.Count(task => task.Status == ProjectTaskStatus.Blocked),
                CancelledTasks = x.Tasks.Count(task => task.Status == ProjectTaskStatus.Cancelled),
                OverdueTasks = x.Tasks.Count(task =>
                    task.DueDate != null && task.DueDate < today && task.Status != ProjectTaskStatus.Done && task.Status != ProjectTaskStatus.Cancelled)
            })
            .ToListAsync(cancellationToken);

        var result = rows.Select(x =>
        {
            var executableTasks = x.TotalTasks - x.CancelledTasks;
            var completionPercent = executableTasks == 0
                ? 0m
                : Math.Round(100m * x.DoneTasks / executableTasks, 2, MidpointRounding.AwayFromZero);
            return new ProjectProgressResponse(
                x.Id,
                x.Code,
                x.Name,
                x.Status,
                x.DueDate,
                x.ActiveMembers,
                x.TotalTasks,
                x.OpenTasks,
                x.DoneTasks,
                x.BlockedTasks,
                x.CancelledTasks,
                x.OverdueTasks,
                completionPercent);
        }).ToArray();

        return OperationResult<IReadOnlyCollection<ProjectProgressResponse>>.Success(result);
    }

    public async Task<OperationResult<IReadOnlyCollection<EmployeeWorkloadResponse>>> GetEmployeeWorkloadAsync(
        Guid? departmentId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (departmentId.HasValue && !await dbContext.Departments.AsNoTracking().AnyAsync(x => x.Id == departmentId.Value, cancellationToken))
        {
            return OperationResult<IReadOnlyCollection<EmployeeWorkloadResponse>>.NotFound("department_not_found", "Department was not found.");
        }

        limit = Math.Clamp(limit, 1, 100);
        var employeeQuery = dbContext.Employees.AsNoTracking().Where(x => x.IsActive);
        if (departmentId.HasValue)
        {
            employeeQuery = employeeQuery.Where(x => x.DepartmentId == departmentId.Value);
        }

        var employees = await employeeQuery
            .OrderBy(x => x.NormalizedFullName)
            .ThenBy(x => x.NormalizedEmployeeCode)
            .Select(x => new
            {
                x.Id,
                x.EmployeeCode,
                x.FullName,
                x.DepartmentId,
                DepartmentName = x.Department == null ? null : x.Department.Name
            })
            .ToListAsync(cancellationToken);

        if (employees.Count == 0)
        {
            return OperationResult<IReadOnlyCollection<EmployeeWorkloadResponse>>.Success(Array.Empty<EmployeeWorkloadResponse>());
        }

        var employeeIds = employees.Select(x => x.Id).ToArray();
        var today = Today();

        var taskRows = await dbContext.ProjectTasks.AsNoTracking()
            .Where(x =>
                x.AssigneeEmployeeId != null &&
                employeeIds.Contains(x.AssigneeEmployeeId.Value) &&
                x.Status != ProjectTaskStatus.Done &&
                x.Status != ProjectTaskStatus.Cancelled)
            .GroupBy(x => x.AssigneeEmployeeId!.Value)
            .Select(group => new
            {
                EmployeeId = group.Key,
                OpenTasks = group.Count(),
                UrgentOpenTasks = group.Count(x => x.Priority == ProjectTaskPriority.Urgent),
                OverdueTasks = group.Count(x => x.DueDate != null && x.DueDate < today)
            })
            .ToListAsync(cancellationToken);

        var surveyRows = await dbContext.SurveyAssignments.AsNoTracking()
            .Where(x =>
                employeeIds.Contains(x.EmployeeId) &&
                x.Status != SurveyAssignmentStatus.Approved &&
                x.Status != SurveyAssignmentStatus.Cancelled)
            .GroupBy(x => x.EmployeeId)
            .Select(group => new
            {
                EmployeeId = group.Key,
                ActiveSurveyAssignments = group.Count(),
                OverdueSurveyAssignments = group.Count(x => x.DueDate != null && x.DueDate < today)
            })
            .ToListAsync(cancellationToken);

        var taskByEmployee = taskRows.ToDictionary(x => x.EmployeeId);
        var surveyByEmployee = surveyRows.ToDictionary(x => x.EmployeeId);
        var result = employees
            .Select(employee =>
            {
                taskByEmployee.TryGetValue(employee.Id, out var taskMetric);
                surveyByEmployee.TryGetValue(employee.Id, out var surveyMetric);
                var openTaskCount = taskMetric?.OpenTasks ?? 0;
                var activeSurveyCount = surveyMetric?.ActiveSurveyAssignments ?? 0;
                return new EmployeeWorkloadResponse(
                    employee.Id,
                    employee.EmployeeCode,
                    employee.FullName,
                    employee.DepartmentId,
                    employee.DepartmentName,
                    openTaskCount,
                    taskMetric?.UrgentOpenTasks ?? 0,
                    taskMetric?.OverdueTasks ?? 0,
                    activeSurveyCount,
                    surveyMetric?.OverdueSurveyAssignments ?? 0,
                    openTaskCount + activeSurveyCount);
            })
            .OrderByDescending(x => x.TotalOpenItems)
            .ThenByDescending(x => x.OverdueTasks + x.OverdueSurveyAssignments)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();

        return OperationResult<IReadOnlyCollection<EmployeeWorkloadResponse>>.Success(result);
    }

    public async Task<OperationResult<IReadOnlyCollection<SurveyProgressResponse>>> GetSurveyProgressAsync(
        Guid? projectId,
        CancellationToken cancellationToken)
    {
        if (projectId.HasValue && !await dbContext.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId.Value, cancellationToken))
        {
            return OperationResult<IReadOnlyCollection<SurveyProgressResponse>>.NotFound("project_not_found", "Project was not found.");
        }

        var today = Today();
        var query = dbContext.SurveyForms.AsNoTracking();
        if (projectId.HasValue)
        {
            query = query.Where(x => x.ProjectId == projectId.Value);
        }

        var rows = await query
            .OrderBy(x => x.Project.NormalizedCode)
            .ThenBy(x => x.NormalizedName)
            .Select(x => new
            {
                x.Id,
                x.ProjectId,
                ProjectCode = x.Project.Code,
                SurveyCode = x.Code,
                SurveyName = x.Name,
                x.Status,
                TotalAssignments = x.Assignments.Count,
                Assigned = x.Assignments.Count(assignment => assignment.Status == SurveyAssignmentStatus.Assigned),
                InProgress = x.Assignments.Count(assignment => assignment.Status == SurveyAssignmentStatus.InProgress),
                PendingReview = x.Assignments.Count(assignment => assignment.Status == SurveyAssignmentStatus.Submitted),
                Approved = x.Assignments.Count(assignment => assignment.Status == SurveyAssignmentStatus.Approved),
                Rejected = x.Assignments.Count(assignment => assignment.Status == SurveyAssignmentStatus.Rejected),
                Cancelled = x.Assignments.Count(assignment => assignment.Status == SurveyAssignmentStatus.Cancelled),
                Overdue = x.Assignments.Count(assignment =>
                    assignment.DueDate != null && assignment.DueDate < today &&
                    assignment.Status != SurveyAssignmentStatus.Approved && assignment.Status != SurveyAssignmentStatus.Cancelled)
            })
            .ToListAsync(cancellationToken);

        var result = rows.Select(x =>
        {
            var actionableAssignments = x.TotalAssignments - x.Cancelled;
            var approvalPercent = actionableAssignments == 0
                ? 0m
                : Math.Round(100m * x.Approved / actionableAssignments, 2, MidpointRounding.AwayFromZero);
            return new SurveyProgressResponse(
                x.Id,
                x.ProjectId,
                x.ProjectCode,
                x.SurveyCode,
                x.SurveyName,
                x.Status,
                x.TotalAssignments,
                x.Assigned,
                x.InProgress,
                x.PendingReview,
                x.Approved,
                x.Rejected,
                x.Cancelled,
                x.Overdue,
                approvalPercent);
        }).ToArray();

        return OperationResult<IReadOnlyCollection<SurveyProgressResponse>>.Success(result);
    }

    private (DateOnly From, DateOnly To, ApiOperationError? Error) ResolveRange(DateOnly? from, DateOnly? to)
    {
        var resolvedTo = to ?? Today();
        var resolvedFrom = from ?? resolvedTo.AddDays(-29);
        if (resolvedFrom > resolvedTo)
        {
            return (default, default, new ApiOperationError("report_date_range_invalid", "Report start date cannot be after the end date."));
        }

        if (resolvedTo.DayNumber - resolvedFrom.DayNumber >= MaximumReportDays)
        {
            return (default, default, new ApiOperationError("report_date_range_too_large", $"Report date range cannot exceed {MaximumReportDays} days."));
        }

        return (resolvedFrom, resolvedTo, null);
    }

    private DateOnly Today() => DateOnly.FromDateTime(UtcNow());
    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static DateTime ToUtc(DateOnly date, TimeOnly time) => DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Utc);
}
