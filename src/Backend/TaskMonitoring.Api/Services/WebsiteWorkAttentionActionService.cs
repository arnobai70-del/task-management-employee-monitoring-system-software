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
}

public sealed class WebsiteWorkAttentionActionService(
    AppDbContext dbContext,
    TimeProvider timeProvider) : IWebsiteWorkAttentionActionService
{
    public const string AcknowledgedAction = "website-work.attention.acknowledged";
    public const string SnoozedAction = "website-work.attention.snoozed";
    public const string FollowUpAssignedAction = "website-work.attention.follow-up-assigned";

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

    private async Task<(ProjectTask? Task, User? ActorUser, OperationResult<WebsiteWorkAttentionActionResponse>? Error)> ResolveContextAsync(
        Guid taskId,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, null, OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(
                "actor_required",
                "A valid authenticated manager is required."));
        }

        var actorUser = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == actor.UserId.Value && user.IsActive, cancellationToken);
        if (actorUser is null)
        {
            return (null, null, OperationResult<WebsiteWorkAttentionActionResponse>.Invalid(
                "actor_invalid",
                "The authenticated manager account is inactive or unavailable."));
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

        return (task, actorUser, null);
    }

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
