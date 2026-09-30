using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkReviewService
{
    Task<IReadOnlyCollection<WebsiteWorkResponse>> EnrichAsync(
        IReadOnlyCollection<WebsiteWorkResponse> items,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkResponse>> SubmitAsync(
        Guid id,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkReviewResponse>> ApproveAsync(
        Guid id,
        ApproveWebsiteWorkRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkReviewResponse>> ReopenAsync(
        Guid id,
        ReopenWebsiteWorkRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);
}

public sealed class WebsiteWorkReviewService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IWebsiteWorkRealtimePublisher realtimePublisher,
    ILogger<WebsiteWorkReviewService> logger) : IWebsiteWorkReviewService
{
    public const string ApprovedAction = "website-work.approved";
    public const string ReopenedAction = "website-work.reopened";

    public async Task<IReadOnlyCollection<WebsiteWorkResponse>> EnrichAsync(
        IReadOnlyCollection<WebsiteWorkResponse> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var ids = items.Select(x => x.Id).Distinct().ToArray();
        var activities = await dbContext.TaskActivities
            .AsNoTracking()
            .Where(x => ids.Contains(x.ProjectTaskId) && IsReviewAction(x.Action))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var metadata = activities
            .GroupBy(x => x.ProjectTaskId)
            .ToDictionary(x => x.Key, x => ResolveReview(x));

        return items
            .Select(item => metadata.TryGetValue(item.Id, out var review)
                ? item with
                {
                    ReviewState = review.State,
                    ReviewComment = review.Comment,
                    SubmittedAtUtc = review.SubmittedAtUtc,
                    ReviewedAtUtc = review.ReviewedAtUtc
                }
                : item)
            .ToArray();
    }

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
                    ? "This completion is already waiting for manager review."
                    : "Start the website work before submitting completion for review.");
        }

        var now = UtcNow();
        task.Status = ProjectTaskStatus.Blocked;
        task.CompletedAtUtc = now;
        task.UpdatedAtUtc = now;

        var completion = new WebsiteWorkCompletionResponse(
            task.Id,
            task.ProjectId,
            task.Project.Name,
            employee.Id,
            employee.EmployeeCode,
            employee.FullName,
            task.Title,
            now,
            $"{employee.FullName} submitted completion: {task.Title}");

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
        AddAudit(actor, "website-work.completion-submitted", task.Id, new
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
            await realtimePublisher.PublishCompletionAsync(completion, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Website work submission {TaskId} was saved but manager realtime delivery failed.", task.Id);
        }

        var response = ToResponse(task);
        var enriched = await EnrichAsync([response], cancellationToken);
        return OperationResult<WebsiteWorkResponse>.Success(enriched.Single());
    }

    public async Task<OperationResult<WebsiteWorkReviewResponse>> ApproveAsync(
        Guid id,
        ApproveWebsiteWorkRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var task = await WorkQuery(tracked: true).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
        {
            return OperationResult<WebsiteWorkReviewResponse>.NotFound("website_work_not_found", "Website work assignment was not found.");
        }

        var current = ResolveReview(task.Activities);
        if (task.Status != ProjectTaskStatus.Blocked || current.State != WebsiteWorkReviewStates.PendingReview)
        {
            return OperationResult<WebsiteWorkReviewResponse>.Conflict(
                "website_work_not_pending_review",
                "Only a completion waiting for review can be approved.");
        }

        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is { Length: > 1000 })
        {
            return OperationResult<WebsiteWorkReviewResponse>.Invalid("website_work_review_comment_too_long", "Review comment cannot exceed 1000 characters.");
        }

        var now = UtcNow();
        task.Status = ProjectTaskStatus.Done;
        task.UpdatedAtUtc = now;
        AddActivity(task, actor.UserId, ApprovedAction, new { comment });
        AddAudit(actor, "website-work.approved", task.Id, new
        {
            task.ProjectId,
            task.AssigneeEmployeeId,
            task.Title,
            comment
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<WebsiteWorkReviewResponse>.Success(new WebsiteWorkReviewResponse(
            task.Id,
            WebsiteWorkReviewStates.Approved,
            comment,
            current.SubmittedAtUtc,
            now));
    }

    public async Task<OperationResult<WebsiteWorkReviewResponse>> ReopenAsync(
        Guid id,
        ReopenWebsiteWorkRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var task = await WorkQuery(tracked: true).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
        {
            return OperationResult<WebsiteWorkReviewResponse>.NotFound("website_work_not_found", "Website work assignment was not found.");
        }

        var current = ResolveReview(task.Activities);
        if (task.Status != ProjectTaskStatus.Blocked || current.State != WebsiteWorkReviewStates.PendingReview)
        {
            return OperationResult<WebsiteWorkReviewResponse>.Conflict(
                "website_work_not_pending_review",
                "Only a completion waiting for review can be reopened.");
        }

        var comment = request.Comment.Trim();
        if (comment.Length is < 3 or > 1000)
        {
            return OperationResult<WebsiteWorkReviewResponse>.Invalid(
                "website_work_reopen_comment_invalid",
                "A correction comment between 3 and 1000 characters is required.");
        }

        var now = UtcNow();
        task.Status = ProjectTaskStatus.InProgress;
        task.CompletedAtUtc = null;
        task.UpdatedAtUtc = now;
        AddActivity(task, actor.UserId, ReopenedAction, new { comment });
        AddAudit(actor, "website-work.reopened", task.Id, new
        {
            task.ProjectId,
            task.AssigneeEmployeeId,
            task.Title,
            comment
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<WebsiteWorkReviewResponse>.Success(new WebsiteWorkReviewResponse(
            task.Id,
            WebsiteWorkReviewStates.CorrectionRequired,
            comment,
            current.SubmittedAtUtc,
            now));
    }

    public static string ResolveState(IEnumerable<TaskActivity> activities)
        => ResolveReview(activities).State;

    private IQueryable<ProjectTask> WorkQuery(bool tracked = false)
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

    private static ReviewMetadata ResolveReview(IEnumerable<TaskActivity> activities)
    {
        var relevant = activities
            .Where(x => IsReviewAction(x.Action))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => ReviewActionRank(x.Action))
            .ToArray();

        var submittedAt = relevant
            .Where(x => x.Action == WebsiteWorkService.CompletedAction)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .FirstOrDefault();

        var latest = relevant.FirstOrDefault();
        if (latest is null)
        {
            return new ReviewMetadata(WebsiteWorkReviewStates.NotSubmitted, null, null, null);
        }

        return latest.Action switch
        {
            ApprovedAction => new ReviewMetadata(
                WebsiteWorkReviewStates.Approved,
                ReadComment(latest),
                submittedAt,
                latest.CreatedAtUtc),
            ReopenedAction => new ReviewMetadata(
                WebsiteWorkReviewStates.CorrectionRequired,
                ReadComment(latest),
                submittedAt,
                latest.CreatedAtUtc),
            _ => new ReviewMetadata(
                WebsiteWorkReviewStates.PendingReview,
                null,
                submittedAt,
                null)
        };
    }

    private static bool IsReviewAction(string action)
        => action == WebsiteWorkService.CompletedAction || action == ApprovedAction || action == ReopenedAction;

    private static int ReviewActionRank(string action)
        => action == ReopenedAction ? 3 : action == ApprovedAction ? 2 : 1;

    private static string? ReadComment(TaskActivity activity)
    {
        try
        {
            using var json = JsonDocument.Parse(activity.DetailsJson);
            return json.RootElement.TryGetProperty("comment", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record ReviewMetadata(
        string State,
        string? Comment,
        DateTime? SubmittedAtUtc,
        DateTime? ReviewedAtUtc);
}
