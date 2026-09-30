using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkAttentionActionService
{
    Task<IReadOnlyCollection<WebsiteWorkAttentionFollowUpOwnerResponse>> GetFollowUpOwnersAsync(CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkFollowUpInboxResponse>> GetMyFollowUpsAsync(
        RequestActor actor,
        bool includeResolved,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkAttentionActionResponse>> AcknowledgeAsync(
        Guid taskId,
        WebsiteWorkAttentionAcknowledgeRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkAttentionActionResponse>> SnoozeAsync(
        Guid taskId,
        WebsiteWorkAttentionSnoozeRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkAttentionActionResponse>> AssignFollowUpAsync(
        Guid taskId,
        WebsiteWorkAttentionFollowUpRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkAttentionActionResponse>> ResolveFollowUpAsync(
        Guid taskId,
        WebsiteWorkAttentionResolveFollowUpRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);
}

public sealed class WebsiteWorkAttentionActionService(
    AppDbContext dbContext,
    TimeProvider timeProvider) : IWebsiteWorkAttentionActionService
{
    public const string AcknowledgedAction = "website-work.attention.acknowledged";
    public const string SnoozedAction = "website-work.attention.snoozed";
    public const string FollowUpAssignedAction = "website-work.attention.follow-up-assigned";
    public const string FollowUpResolvedAction = "website-work.attention.follow-up-resolved";

    public async Task<IReadOnlyCollection<WebsiteWorkAttentionFollowUpOwnerResponse>> GetFollowUpOwnersAsync(
        CancellationToken cancellationToken)
        => await dbContext.Users
            .AsNoTracking()
            .Where(user =>
                user.IsActive &&
                user.UserRoles.Any(userRole =>
                    userRole.Role.IsActive &&
                    userRole.Role.RolePermissions.Any(rolePermission =>
                        rolePermission.Permission.Code == PermissionCatalog.TasksManage)))
            .OrderBy(user => user.Employee != null ? user.Employee.FullName : user.Email)
            .ThenBy(user => user.Email)
            .Select(user => new WebsiteWorkAttentionFollowUpOwnerResponse(
                user.Id,
                user.Email,
                user.Employee != null ? user.Employee.FullName : null))
            .ToArrayAsync(cancellationToken);

    public async Task<OperationResult<WebsiteWorkFollowUpInboxResponse>> GetMyFollowUpsAsync(
        RequestActor actor,
        bool includeResolved,
        CancellationToken cancellationToken)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return OperationResult<WebsiteWorkFollowUpInboxResponse>.Invalid(
                actorResult.Error.Code,
                actorResult.Error.Message);
        }

        var now = UtcNow();
        var tasks = await dbContext.ProjectTasks
            .AsNoTracking()
            .Include(task => task.Project)
            .Include(task => task.AssigneeEmployee)
            .Include(task => task.Activities)
            .Where(task =>
                task.Status != ProjectTaskStatus.Done &&
                task.Status != ProjectTaskStatus.Cancelled &&
                task.Activities.Any(activity => activity.Action == WebsiteWorkService.ConfiguredAction) &&
                task.Activities.Any(activity =>
                    activity.Action == FollowUpAssignedAction ||
                    activity.Action == FollowUpResolvedAction))
            .ToArrayAsync(cancellationToken);

        var allForActor = tasks
            .Select(task => BuildFollowUpInboxItem(task, actorResult.User!.Id, now))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();

        var visible = allForActor
            .Where(item => includeResolved || item.State != WebsiteWorkFollowUpState.Resolved)
            .OrderBy(item => item.State == WebsiteWorkFollowUpState.Overdue ? 0 : item.State == WebsiteWorkFollowUpState.Pending ? 1 : 2)
            .ThenBy(item => item.State == WebsiteWorkFollowUpState.Resolved ? DateTime.MaxValue : item.DueAtUtc)
            .ThenByDescending(item => item.ResolvedAtUtc)
            .ThenBy(item => item.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return OperationResult<WebsiteWorkFollowUpInboxResponse>.Success(
            new WebsiteWorkFollowUpInboxResponse(
                now,
                allForActor.Count(item => item.State == WebsiteWorkFollowUpState.Pending),
                allForActor.Count(item => item.State == WebsiteWorkFollowUpState.Overdue),
                allForActor.Count(item => item.State == WebsiteWorkFollowUpState.Resolved),
                visible.Length,
                visible));
    }

    public async Task<OperationResult<WebsiteWorkAttentionActionResponse>> AcknowledgeAsync(
        Guid taskId,
        WebsiteWorkAttentionAcknowledgeRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(taskId, actor, cancellationToken);
        if (context.Error is not null)
        {
            return context.Error;
        }

        var noteResult = NormalizeOptionalNote(request.Note, 500);
        if (noteResult.Error is not null)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(noteResult.Error.Code, noteResult.Error.Message);
        }

        var now = UtcNow();
        AddManagementActivity(context.Task!, context.ActorUser!, AcknowledgedAction, new
        {
            actorUserId = context.ActorUser!.Id,
            actorEmail = context.ActorUser.Email,
            note = noteResult.Value,
            acknowledgedAtUtc = now
        }, now);
        AddAudit(actor, "website-work.attention.acknowledged", taskId, new
        {
            context.Task!.ProjectId,
            context.Task.AssigneeEmployeeId,
            context.Task.Title,
            note = noteResult.Value
        }, now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<WebsiteWorkAttentionActionResponse>.Success(
            new WebsiteWorkAttentionActionResponse(
                taskId,
                WebsiteWorkAttentionDisposition.Acknowledged,
                now,
                "Attention item acknowledged until the Website Work lifecycle changes."));
    }

    public async Task<OperationResult<WebsiteWorkAttentionActionResponse>> SnoozeAsync(
        Guid taskId,
        WebsiteWorkAttentionSnoozeRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (request.Minutes is < 15 or > 10080)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(
                "snooze_minutes_invalid",
                "Snooze duration must be between 15 minutes and 7 days.");
        }

        var context = await ResolveContextAsync(taskId, actor, cancellationToken);
        if (context.Error is not null)
        {
            return context.Error;
        }

        var noteResult = NormalizeOptionalNote(request.Note, 500);
        if (noteResult.Error is not null)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(noteResult.Error.Code, noteResult.Error.Message);
        }

        var now = UtcNow();
        var untilUtc = now.AddMinutes(request.Minutes);
        AddManagementActivity(context.Task!, context.ActorUser!, SnoozedAction, new
        {
            actorUserId = context.ActorUser!.Id,
            actorEmail = context.ActorUser.Email,
            note = noteResult.Value,
            snoozedUntilUtc = untilUtc
        }, now);
        AddAudit(actor, "website-work.attention.snoozed", taskId, new
        {
            context.Task!.ProjectId,
            context.Task.AssigneeEmployeeId,
            context.Task.Title,
            request.Minutes,
            snoozedUntilUtc = untilUtc,
            note = noteResult.Value
        }, now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<WebsiteWorkAttentionActionResponse>.Success(
            new WebsiteWorkAttentionActionResponse(
                taskId,
                WebsiteWorkAttentionDisposition.Snoozed,
                now,
                $"Attention item snoozed until {untilUtc:O}."));
    }

    public async Task<OperationResult<WebsiteWorkAttentionActionResponse>> AssignFollowUpAsync(
        Guid taskId,
        WebsiteWorkAttentionFollowUpRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var note = request.Note?.Trim();
        if (string.IsNullOrWhiteSpace(note) || note.Length < 2 || note.Length > 1000)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(
                "follow_up_note_invalid",
                "Follow-up note must be between 2 and 1000 characters.");
        }

        if (request.DueAtUtc.Kind == DateTimeKind.Unspecified)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(
                "follow_up_due_timezone_required",
                "Follow-up due time must include a timezone or UTC offset.");
        }

        var now = UtcNow();
        var dueAtUtc = request.DueAtUtc.ToUniversalTime();
        if (dueAtUtc <= now.AddMinutes(5) || dueAtUtc > now.AddDays(30))
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(
                "follow_up_due_invalid",
                "Follow-up due time must be more than 5 minutes from now and no more than 30 days away.");
        }

        var context = await ResolveContextAsync(taskId, actor, cancellationToken);
        if (context.Error is not null)
        {
            return context.Error;
        }

        var owner = await dbContext.Users
            .AsNoTracking()
            .Include(user => user.Employee)
            .SingleOrDefaultAsync(user =>
                user.Id == request.OwnerUserId &&
                user.IsActive &&
                user.UserRoles.Any(userRole =>
                    userRole.Role.IsActive &&
                    userRole.Role.RolePermissions.Any(rolePermission =>
                        rolePermission.Permission.Code == PermissionCatalog.TasksManage)),
                cancellationToken);
        if (owner is null)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(
                "follow_up_owner_invalid",
                "Follow-up owner must be an active user with Website Work management permission.");
        }

        AddManagementActivity(context.Task!, context.ActorUser!, FollowUpAssignedAction, new
        {
            actorUserId = context.ActorUser!.Id,
            actorEmail = context.ActorUser.Email,
            note,
            followUpOwnerUserId = owner.Id,
            followUpOwnerEmail = owner.Email,
            followUpOwnerName = owner.Employee?.FullName,
            followUpDueAtUtc = dueAtUtc
        }, now);
        AddAudit(actor, "website-work.attention.follow-up-assigned", taskId, new
        {
            context.Task!.ProjectId,
            context.Task.AssigneeEmployeeId,
            context.Task.Title,
            followUpOwnerUserId = owner.Id,
            followUpOwnerEmail = owner.Email,
            followUpDueAtUtc = dueAtUtc,
            note
        }, now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<WebsiteWorkAttentionActionResponse>.Success(
            new WebsiteWorkAttentionActionResponse(
                taskId,
                WebsiteWorkAttentionDisposition.FollowUp,
                now,
                $"Follow-up assigned to {owner.Employee?.FullName ?? owner.Email} until {dueAtUtc:O}."));
    }

    public async Task<OperationResult<WebsiteWorkAttentionActionResponse>> ResolveFollowUpAsync(
        Guid taskId,
        WebsiteWorkAttentionResolveFollowUpRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(taskId, actor, cancellationToken);
        if (context.Error is not null)
        {
            return context.Error;
        }

        var noteResult = NormalizeOptionalNote(request.Note, 1000);
        if (noteResult.Error is not null)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(noteResult.Error.Code, noteResult.Error.Message);
        }

        var currentManagement = FindCurrentManagementAction(context.Task!.Activities);
        if (currentManagement is null || currentManagement.Action != FollowUpAssignedAction)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Conflict(
                "follow_up_not_current",
                "This Website Work target does not have a current unresolved follow-up assignment.");
        }

        var assignmentDetails = ParseDetails(currentManagement.DetailsJson);
        var ownerUserId = ReadGuid(assignmentDetails, "followUpOwnerUserId");
        if (!ownerUserId.HasValue || ownerUserId.Value != context.ActorUser!.Id)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Conflict(
                "follow_up_not_owned",
                "Only the manager currently assigned to this follow-up can resolve it.");
        }

        var dueAtUtc = ReadDateTime(assignmentDetails, "followUpDueAtUtc");
        if (!dueAtUtc.HasValue)
        {
            return OperationResult<WebsiteWorkAttentionActionResponse>.Conflict(
                "follow_up_invalid",
                "The current follow-up assignment is missing its due time and cannot be resolved safely.");
        }

        var now = UtcNow();
        var assignmentNote = ReadString(assignmentDetails, "note") ?? string.Empty;
        var ownerEmail = ReadString(assignmentDetails, "followUpOwnerEmail") ?? context.ActorUser.Email;
        var ownerName = ReadString(assignmentDetails, "followUpOwnerName");
        AddManagementActivity(context.Task, context.ActorUser, FollowUpResolvedAction, new
        {
            actorUserId = context.ActorUser.Id,
            actorEmail = context.ActorUser.Email,
            note = noteResult.Value,
            followUpOwnerUserId = context.ActorUser.Id,
            followUpOwnerEmail = ownerEmail,
            followUpOwnerName = ownerName,
            followUpAssignedAtUtc = currentManagement.CreatedAtUtc,
            followUpAssignmentNote = assignmentNote,
            followUpDueAtUtc = dueAtUtc.Value,
            resolvedAtUtc = now
        }, now);
        AddAudit(actor, "website-work.attention.follow-up-resolved", taskId, new
        {
            context.Task.ProjectId,
            context.Task.AssigneeEmployeeId,
            context.Task.Title,
            followUpOwnerUserId = context.ActorUser.Id,
            followUpDueAtUtc = dueAtUtc.Value,
            resolutionNote = noteResult.Value
        }, now);

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<WebsiteWorkAttentionActionResponse>.Success(
            new WebsiteWorkAttentionActionResponse(
                taskId,
                WebsiteWorkAttentionDisposition.Resolved,
                now,
                "Follow-up resolved. The current attention signal stays managed until the Website Work lifecycle changes."));
    }

    private async Task<(User? User, ApiOperationError? Error)> ResolveActorAsync(
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, new ApiOperationError("actor_required", "A valid authenticated manager is required."));
        }

        var actorUser = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == actor.UserId.Value && user.IsActive, cancellationToken);
        return actorUser is null
            ? (null, new ApiOperationError("actor_invalid", "The authenticated manager account is inactive or unavailable."))
            : (actorUser, null);
    }

    private async Task<(ProjectTask? Task, User? ActorUser, OperationResult<WebsiteWorkAttentionActionResponse>? Error)> ResolveContextAsync(
        Guid taskId,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var actorResult = await ResolveActorAsync(actor, cancellationToken);
        if (actorResult.Error is not null)
        {
            return (null, null, OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(
                actorResult.Error.Code,
                actorResult.Error.Message));
        }

        var task = await dbContext.ProjectTasks
            .Include(x => x.Project)
            .Include(x => x.AssigneeEmployee)
            .Include(x => x.Activities)
            .SingleOrDefaultAsync(x =>
                x.Id == taskId &&
                x.Activities.Any(activity => activity.Action == WebsiteWorkService.ConfiguredAction),
                cancellationToken);
        if (task is null)
        {
            return (null, null, OperationResult<WebsiteWorkAttentionActionResponse>.NotFound(
                "website_work_not_found",
                "Website Work assignment was not found."));
        }

        if (task.Status is ProjectTaskStatus.Done or ProjectTaskStatus.Cancelled)
        {
            return (null, null, OperationResult<WebsiteWorkAttentionActionResponse>.Conflict(
                "website_work_closed",
                "Closed Website Work does not need an attention action."));
        }

        return (task, actorResult.User, null);
    }

    private static WebsiteWorkFollowUpInboxItemResponse? BuildFollowUpInboxItem(
        ProjectTask task,
        Guid actorUserId,
        DateTime now)
    {
        if (task.AssigneeEmployee is null)
        {
            return null;
        }

        var current = FindCurrentManagementAction(task.Activities);
        if (current is null ||
            current.Action is not (FollowUpAssignedAction or FollowUpResolvedAction))
        {
            return null;
        }

        if (current.Action == FollowUpAssignedAction)
        {
            var details = ParseDetails(current.DetailsJson);
            var ownerUserId = ReadGuid(details, "followUpOwnerUserId");
            var dueAtUtc = ReadDateTime(details, "followUpDueAtUtc");
            if (ownerUserId != actorUserId || !dueAtUtc.HasValue)
            {
                return null;
            }

            return new WebsiteWorkFollowUpInboxItemResponse(
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
                dueAtUtc.Value <= now ? WebsiteWorkFollowUpState.Overdue : WebsiteWorkFollowUpState.Pending,
                current.CreatedAtUtc,
                ReadGuid(details, "actorUserId") ?? current.ActorUserId,
                ReadString(details, "actorEmail"),
                ReadString(details, "note") ?? string.Empty,
                dueAtUtc.Value,
                null,
                null,
                null,
                null);
        }

        var resolutionDetails = ParseDetails(current.DetailsJson);
        var resolvedOwnerUserId = ReadGuid(resolutionDetails, "followUpOwnerUserId");
        var resolvedDueAtUtc = ReadDateTime(resolutionDetails, "followUpDueAtUtc");
        if (resolvedOwnerUserId != actorUserId || !resolvedDueAtUtc.HasValue)
        {
            return null;
        }

        return new WebsiteWorkFollowUpInboxItemResponse(
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
            WebsiteWorkFollowUpState.Resolved,
            ReadDateTime(resolutionDetails, "followUpAssignedAtUtc") ?? current.CreatedAtUtc,
            null,
            null,
            ReadString(resolutionDetails, "followUpAssignmentNote") ?? string.Empty,
            resolvedDueAtUtc.Value,
            ReadDateTime(resolutionDetails, "resolvedAtUtc") ?? current.CreatedAtUtc,
            ReadGuid(resolutionDetails, "actorUserId") ?? current.ActorUserId,
            ReadString(resolutionDetails, "actorEmail"),
            ReadString(resolutionDetails, "note"));
    }

    private static TaskActivity? FindCurrentManagementAction(IEnumerable<TaskActivity> activities)
    {
        var ordered = activities
            .OrderBy(activity => activity.CreatedAtUtc)
            .ThenBy(activity => activity.Id)
            .ToArray();
        var lifecycleAtUtc = ordered
            .Where(activity => IsLifecycleAction(activity.Action))
            .Select(activity => (DateTime?)activity.CreatedAtUtc)
            .Max();

        return ordered
            .Where(activity => IsManagementAction(activity.Action))
            .Where(activity => !lifecycleAtUtc.HasValue || activity.CreatedAtUtc > lifecycleAtUtc.Value)
            .OrderByDescending(activity => activity.CreatedAtUtc)
            .ThenByDescending(activity => activity.Id)
            .FirstOrDefault();
    }

    private static bool IsLifecycleAction(string action)
        => action == WebsiteWorkService.ConfiguredAction ||
           action == WebsiteWorkService.StartedAction ||
           action == WebsiteWorkReviewService.SubmittedAction ||
           action == WebsiteWorkReviewService.ReopenedAction ||
           action == WebsiteWorkReviewService.ApprovedAction ||
           action == WebsiteWorkService.CompletedAction;

    private static bool IsManagementAction(string action)
        => action == AcknowledgedAction ||
           action == SnoozedAction ||
           action == FollowUpAssignedAction ||
           action == FollowUpResolvedAction;

    private static (string? Value, ApiOperationError? Error) NormalizeOptionalNote(string? value, int maximumLength)
    {
        var note = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (note is { Length: > 0 } && note.Length > maximumLength)
        {
            return (null, new ApiOperationError(
                "attention_note_too_long",
                $"Attention note cannot exceed {maximumLength} characters."));
        }

        return (note, null);
    }

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

    private void AddManagementActivity(ProjectTask task, User actorUser, string action, object details, DateTime now)
    {
        var activity = new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            ActorUserId = actorUser.Id,
            Action = action,
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAtUtc = now
        };
        dbContext.TaskActivities.Add(activity);
        task.Activities.Add(activity);
    }

    private void AddAudit(RequestActor actor, string action, Guid taskId, object details, DateTime now)
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
            CreatedAtUtc = now
        });
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
}
