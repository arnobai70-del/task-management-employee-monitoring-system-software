using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkReviewService
{
    Task<OperationResult<WebsiteWorkResponse>> SubmitAsync(Guid id, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<WebsiteWorkResponse>> ApproveAsync(Guid id, WebsiteWorkReviewRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<WebsiteWorkResponse>> ReopenAsync(Guid id, WebsiteWorkReviewRequest request, RequestActor actor, CancellationToken cancellationToken);
}

public sealed class WebsiteWorkReviewService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IWebsiteWorkRealtimePublisher managerPublisher,
    IRealtimeEventPublisher employeePublisher,
    ILogger<WebsiteWorkReviewService> logger) : IWebsiteWorkReviewService
{
    public const string SubmittedAction = "website-work.submitted";
    public const string ApprovedAction = "website-work.approved";
    public const string ReopenedAction = "website-work.reopened";

    public async Task<OperationResult<WebsiteWorkResponse>> SubmitAsync(
        Guid id,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return OperationResult<WebsiteWorkResponse>.Invalid(employeeResult.Error.Code, employeeResult.Error.Message);
        }

        var employee = employeeResult.Employee!;
        var task = await WorkQuery(tracked: true)
            .SingleOrDefaultAsync(x => x.Id == id && x.AssigneeEmployeeId == employee.Id, cancellationToken);
        if (task is null)
        {
            return OperationResult<WebsiteWorkResponse>.NotFound("website_work_not_found", "Website work assignment was not found.");
        }

        if (task.Status != ProjectTaskStatus.InProgress)
        {
            return OperationResult<WebsiteWorkResponse>.Conflict(
                "website_work_not_working",
                task.Status == ProjectTaskStatus.Blocked
                    ? "This website work is already waiting for manager review."
                    : "Start the website work before submitting it for review.");
        }

        var now = UtcNow();
        task.Status = ProjectTaskStatus.Blocked;
        task.CompletedAtUtc = null;
        task.UpdatedAtUtc = now;

        var submission = new WebsiteWorkSubmissionResponse(
            task.Id,
            task.ProjectId,
            task.Project.Name,
            employee.Id,
            employee.EmployeeCode,
            employee.FullName,
            task.Title,
            now,
            $"{employee.FullName} submitted completion: {task.Title}");

        AddActivity(task, actor.UserId, SubmittedAction, new
        {
            taskId = submission.TaskId,
            projectId = submission.ProjectId,
            projectName = submission.ProjectName,
            employeeId = submission.EmployeeId,
            employeeCode = submission.EmployeeCode,
            employeeName = submission.EmployeeName,
            taskTitle = submission.TaskTitle,
            submittedAtUtc = submission.SubmittedAtUtc,
            message = submission.Message
        });
        AddAudit(actor, "website-work.submitted", task.Id, new
        {
            task.ProjectId,
            employee.Id,
            employee.EmployeeCode,
            employee.FullName,
            task.Title
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await managerPublisher.PublishSubmissionAsync(submission, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Website work submission {TaskId} was saved but manager realtime delivery failed.", task.Id);
        }

        return OperationResult<WebsiteWorkResponse>.Success(ToResponse(task));
    }

    public async Task<OperationResult<WebsiteWorkResponse>> ApproveAsync(
        Guid id,
        WebsiteWorkReviewRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return OperationResult<WebsiteWorkResponse>.Invalid("actor_required", "A valid authenticated manager is required.");
        }

        var task = await WorkQuery(tracked: true).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
        {
            return OperationResult<WebsiteWorkResponse>.NotFound("website_work_not_found", "Website work assignment was not found.");
        }

        if (task.Status != ProjectTaskStatus.Blocked)
        {
            return OperationResult<WebsiteWorkResponse>.Conflict("website_work_not_pending_review", "Only website work waiting for review can be approved.");
        }

        if (task.AssigneeEmployee is null)
        {
            return OperationResult<WebsiteWorkResponse>.Conflict("website_work_unassigned", "Website work must have an assigned employee before it can be approved.");
        }

        var comment = NormalizeComment(request.Comment);
        var now = UtcNow();
        task.Status = ProjectTaskStatus.Done;
        task.CompletedAtUtc = now;
        task.UpdatedAtUtc = now;

        AddActivity(task, actor.UserId, ApprovedAction, new { comment, approvedAtUtc = now });
        var completion = new WebsiteWorkCompletionResponse(
            task.Id,
            task.ProjectId,
            task.Project.Name,
            task.AssigneeEmployee.Id,
            task.AssigneeEmployee.EmployeeCode,
            task.AssigneeEmployee.FullName,
            task.Title,
            now,
            $"{task.AssigneeEmployee.FullName} completed: {task.Title}");
        AddActivity(task, actor.UserId, WebsiteWorkService.CompletedAction, new
        {
            taskId = completion.TaskId,
            projectId = completion.ProjectId,
            projectName = completion.ProjectName,
            employeeId = completion.EmployeeId,
            employeeCode = completion.EmployeeCode,
            employeeName = completion.EmployeeName,
            taskTitle = completion.TaskTitle,
            completedAtUtc = completion.CompletedAtUtc,
            message = completion.Message
        });
        AddAudit(actor, "website-work.approved", task.Id, new
        {
            task.ProjectId,
            task.AssigneeEmployeeId,
            task.Title,
            comment
        });

        var notification = CreateEmployeeNotification(
            task.AssigneeEmployee,
            task,
            "Website work approved",
            string.IsNullOrWhiteSpace(comment)
                ? $"Approved: {task.Title}"
                : $"Approved: {task.Title}. Manager note: {comment}",
            now);
        dbContext.EmployeeNotifications.Add(notification);

        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishEmployeeNotificationSafeAsync(notification, cancellationToken);
        return OperationResult<WebsiteWorkResponse>.Success(ToResponse(task));
    }

    public async Task<OperationResult<WebsiteWorkResponse>> ReopenAsync(
        Guid id,
        WebsiteWorkReviewRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return OperationResult<WebsiteWorkResponse>.Invalid("actor_required", "A valid authenticated manager is required.");
        }

        var comment = NormalizeComment(request.Comment);
        if (string.IsNullOrWhiteSpace(comment) || comment.Length < 2)
        {
            return OperationResult<WebsiteWorkResponse>.Invalid("review_comment_required", "A correction comment of at least 2 characters is required when reopening website work.");
        }

        var task = await WorkQuery(tracked: true).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
        {
            return OperationResult<WebsiteWorkResponse>.NotFound("website_work_not_found", "Website work assignment was not found.");
        }

        if (task.Status != ProjectTaskStatus.Blocked)
        {
            return OperationResult<WebsiteWorkResponse>.Conflict("website_work_not_pending_review", "Only website work waiting for review can be reopened.");
        }

        if (task.AssigneeEmployee is null)
        {
            return OperationResult<WebsiteWorkResponse>.Conflict("website_work_unassigned", "Website work must have an assigned employee before it can be reopened.");
        }

        var now = UtcNow();
        task.Status = ProjectTaskStatus.InProgress;
        task.CompletedAtUtc = null;
        task.UpdatedAtUtc = now;

        AddActivity(task, actor.UserId, ReopenedAction, new { comment, reopenedAtUtc = now });
        AddAudit(actor, "website-work.reopened", task.Id, new
        {
            task.ProjectId,
            task.AssigneeEmployeeId,
            task.Title,
            comment
        });

        var notification = CreateEmployeeNotification(
            task.AssigneeEmployee,
            task,
            "Website work needs correction",
            $"Correction requested for {task.Title}: {comment}",
            now);
        dbContext.EmployeeNotifications.Add(notification);

        await dbContext.SaveChangesAsync(cancellationToken);
        await PublishEmployeeNotificationSafeAsync(notification, cancellationToken);
        return OperationResult<WebsiteWorkResponse>.Success(ToResponse(task));
    }

    private IQueryable<ProjectTask> WorkQuery(bool tracked)
    {
        var query = dbContext.ProjectTasks
            .Include(x => x.Project)
            .Include(x => x.AssigneeEmployee)
            .Include(x => x.Activities)
            .Where(x => x.Activities.Any(activity => activity.Action == WebsiteWorkService.ConfiguredAction));
        return tracked ? query : query.AsNoTracking();
    }

    private async Task<(Employee? Employee, ApiOperationError? Error)> ResolveEmployeeAsync(
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, new ApiOperationError("authenticated_user_required", "An authenticated user is required."));
        }

        var employee = await dbContext.Employees
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.UserId == actor.UserId.Value, cancellationToken);
        if (employee is null)
        {
            return (null, new ApiOperationError("employee_profile_not_found", "No employee profile is linked to this account."));
        }

        if (!employee.IsActive || !employee.User.IsActive)
        {
            return (null, new ApiOperationError("employee_inactive", "The employee profile is inactive."));
        }

        return (employee, null);
    }

    private static string? NormalizeComment(string? value)
    {
        var comment = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return comment is { Length: > 1000 } ? comment[..1000] : comment;
    }

    private static string? CurrentUrl(ProjectTask task)
    {
        var activity = task.Activities
            .Where(x => x.Action == WebsiteWorkService.ConfiguredAction)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
        if (activity is null)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(activity.DetailsJson);
            return json.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WebsiteWorkResponse ToResponse(ProjectTask task)
    {
        var startedAt = task.Activities
            .Where(x => x.Action == WebsiteWorkService.StartedAction || x.Action == ReopenedAction)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .FirstOrDefault();

        return new WebsiteWorkResponse(
            task.Id,
            task.ProjectId,
            task.Project.Code,
            task.Project.Name,
            task.AssigneeEmployeeId,
            task.AssigneeEmployee?.EmployeeCode,
            task.AssigneeEmployee?.FullName,
            task.Title,
            task.Description,
            CurrentUrl(task) ?? string.Empty,
            task.Status,
            task.Priority,
            task.DueDate,
            startedAt,
            task.CompletedAtUtc,
            task.CreatedAtUtc,
            task.UpdatedAtUtc);
    }

    private void AddActivity(ProjectTask task, Guid? actorUserId, string action, object details)
    {
        var activity = new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            ActorUserId = actorUserId,
            Action = action,
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAtUtc = UtcNow()
        };
        dbContext.TaskActivities.Add(activity);
        task.Activities.Add(activity);
    }

    private void AddAudit(RequestActor actor, string action, Guid taskId, object details)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = "ProjectTask",
            TargetId = taskId.ToString(),
            MetadataJson = JsonSerializer.Serialize(details),
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CreatedAtUtc = UtcNow()
        });
    }

    private static EmployeeNotification CreateEmployeeNotification(
        Employee employee,
        ProjectTask task,
        string title,
        string message,
        DateTime now)
        => new()
        {
            EmployeeId = employee.Id,
            Employee = employee,
            Kind = EmployeeNotificationKind.TaskStatusChanged,
            Title = title,
            Message = message,
            EntityType = "ProjectTask",
            EntityId = task.Id,
            CreatedAtUtc = now
        };

    private async Task PublishEmployeeNotificationSafeAsync(EmployeeNotification notification, CancellationToken cancellationToken)
    {
        var response = new EmployeeNotificationResponse(
            notification.Id,
            notification.Kind,
            notification.Title,
            notification.Message,
            notification.EntityType,
            notification.EntityId,
            notification.CreatedAtUtc,
            notification.ReadAtUtc);
        try
        {
            await employeePublisher.PublishNotificationAsync(notification.EmployeeId, response, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Website work review notification {NotificationId} was saved but realtime delivery failed.", notification.Id);
        }
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
}
