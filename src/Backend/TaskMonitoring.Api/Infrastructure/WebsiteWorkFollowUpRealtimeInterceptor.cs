using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Infrastructure;

public sealed class WebsiteWorkFollowUpRealtimeInterceptor(
    IWebsiteWorkRealtimePublisher realtimePublisher,
    ILogger<WebsiteWorkFollowUpRealtimeInterceptor> logger) : SaveChangesInterceptor
{
    private readonly List<(Guid UserId, WebsiteWorkFollowUpRealtimeResponse Payload)> _pending = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        QueueRealtimeEvents(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await PublishQueuedAsync(cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void QueueRealtimeEvents(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var entries = context.ChangeTracker.Entries<TaskActivity>()
            .Where(entry => entry.State == EntityState.Added && IsRelevantAction(entry.Entity.Action))
            .ToArray();

        foreach (var entry in entries)
        {
            var task = entry.Entity.ProjectTask;
            if (task is null || task.Project is null || task.AssigneeEmployee is null)
            {
                continue;
            }

            var priorActivities = task.Activities
                .Where(activity => !ReferenceEquals(activity, entry.Entity))
                .Where(activity => context.Entry(activity).State != EntityState.Added)
                .ToArray();
            var previousFollowUp = FindCurrentFollowUp(priorActivities);

            if (entry.Entity.Action == WebsiteWorkAttentionActionService.FollowUpAssignedAction)
            {
                QueueAssignment(context, task, entry.Entity, previousFollowUp);
                continue;
            }

            if (entry.Entity.Action == WebsiteWorkAttentionActionService.FollowUpResolvedAction)
            {
                QueueResolved(context, task, entry.Entity);
                continue;
            }

            if (previousFollowUp is not null)
            {
                var message = $"Follow-up is no longer current: {task.AssigneeEmployee.FullName} · {task.Title}";
                var payload = BuildPayload(
                    WebsiteWorkFollowUpRealtimeAction.Removed,
                    task,
                    previousFollowUp.OwnerUserId,
                    previousFollowUp.OwnerEmail,
                    previousFollowUp.OwnerName,
                    previousFollowUp.DueAtUtc,
                    entry.Entity.CreatedAtUtc,
                    message);
                QueueDelivery(
                    context,
                    task,
                    entry.Entity,
                    previousFollowUp.OwnerUserId,
                    AdminNotificationKind.FollowUpRemoved,
                    "Follow-up changed",
                    message,
                    previousFollowUp.DueAtUtc,
                    payload);
            }
        }
    }

    private void QueueAssignment(
        DbContext context,
        ProjectTask task,
        TaskActivity activity,
        FollowUpSnapshot? previousFollowUp)
    {
        var details = ParseDetails(activity.DetailsJson);
        var ownerUserId = ReadGuid(details, "followUpOwnerUserId");
        var ownerEmail = ReadString(details, "followUpOwnerEmail");
        var dueAtUtc = ReadDateTime(details, "followUpDueAtUtc");
        if (!ownerUserId.HasValue || string.IsNullOrWhiteSpace(ownerEmail) || !dueAtUtc.HasValue)
        {
            return;
        }

        var ownerName = ReadString(details, "followUpOwnerName");
        var sameOwner = previousFollowUp is not null && previousFollowUp.OwnerUserId == ownerUserId.Value;

        if (previousFollowUp is not null && !sameOwner)
        {
            var removedMessage = $"Follow-up reassigned: {task.AssigneeEmployee!.FullName} · {task.Title}";
            var removedPayload = BuildPayload(
                WebsiteWorkFollowUpRealtimeAction.Removed,
                task,
                previousFollowUp.OwnerUserId,
                previousFollowUp.OwnerEmail,
                previousFollowUp.OwnerName,
                previousFollowUp.DueAtUtc,
                activity.CreatedAtUtc,
                removedMessage);
            QueueDelivery(
                context,
                task,
                activity,
                previousFollowUp.OwnerUserId,
                AdminNotificationKind.FollowUpRemoved,
                "Follow-up reassigned",
                removedMessage,
                previousFollowUp.DueAtUtc,
                removedPayload);
        }

        var message = sameOwner
            ? $"Follow-up updated: {task.AssigneeEmployee!.FullName} · {task.Title}"
            : $"Follow-up assigned: {task.AssigneeEmployee!.FullName} · {task.Title}";
        var payload = BuildPayload(
            sameOwner ? WebsiteWorkFollowUpRealtimeAction.Updated : WebsiteWorkFollowUpRealtimeAction.Assigned,
            task,
            ownerUserId.Value,
            ownerEmail,
            ownerName,
            dueAtUtc.Value,
            activity.CreatedAtUtc,
            message);
        QueueDelivery(
            context,
            task,
            activity,
            ownerUserId.Value,
            sameOwner ? AdminNotificationKind.FollowUpUpdated : AdminNotificationKind.FollowUpAssigned,
            sameOwner ? "Follow-up updated" : "New follow-up assigned",
            message,
            dueAtUtc.Value,
            payload);
    }

    private void QueueResolved(DbContext context, ProjectTask task, TaskActivity activity)
    {
        var details = ParseDetails(activity.DetailsJson);
        var ownerUserId = ReadGuid(details, "followUpOwnerUserId");
        var ownerEmail = ReadString(details, "followUpOwnerEmail");
        var dueAtUtc = ReadDateTime(details, "followUpDueAtUtc");
        if (!ownerUserId.HasValue || string.IsNullOrWhiteSpace(ownerEmail) || !dueAtUtc.HasValue)
        {
            return;
        }

        var message = $"Follow-up resolved: {task.AssigneeEmployee!.FullName} · {task.Title}";
        var payload = BuildPayload(
            WebsiteWorkFollowUpRealtimeAction.Resolved,
            task,
            ownerUserId.Value,
            ownerEmail,
            ReadString(details, "followUpOwnerName"),
            dueAtUtc.Value,
            activity.CreatedAtUtc,
            message);
        QueueDelivery(
            context,
            task,
            activity,
            ownerUserId.Value,
            AdminNotificationKind.FollowUpResolved,
            "Follow-up resolved",
            message,
            dueAtUtc.Value,
            payload);
    }

    private void QueueDelivery(
        DbContext context,
        ProjectTask task,
        TaskActivity sourceActivity,
        Guid userId,
        AdminNotificationKind kind,
        string notificationTitle,
        string message,
        DateTime dueAtUtc,
        WebsiteWorkFollowUpRealtimeResponse payload)
    {
        _pending.Add((userId, payload));

        var notification = AdminNotificationService.CreateActivity(
            task,
            userId,
            kind,
            notificationTitle,
            message,
            sourceActivity.Id,
            sourceActivity.CreatedAtUtc,
            dueAtUtc);
        context.Add(notification);
    }

    private static WebsiteWorkFollowUpRealtimeResponse BuildPayload(
        WebsiteWorkFollowUpRealtimeAction action,
        ProjectTask task,
        Guid ownerUserId,
        string ownerEmail,
        string? ownerName,
        DateTime dueAtUtc,
        DateTime occurredAtUtc,
        string message)
        => new(
            action,
            task.Id,
            task.ProjectId,
            task.Project.Name,
            task.AssigneeEmployee!.Id,
            task.AssigneeEmployee.EmployeeCode,
            task.AssigneeEmployee.FullName,
            task.Title,
            ownerUserId,
            ownerEmail,
            ownerName,
            dueAtUtc,
            occurredAtUtc,
            message);

    private async Task PublishQueuedAsync(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var pending = _pending.ToArray();
        _pending.Clear();
        foreach (var item in pending)
        {
            try
            {
                await realtimePublisher.PublishFollowUpAsync(item.UserId, item.Payload, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Website Work follow-up {TaskId} was saved but realtime delivery to user {UserId} failed.",
                    item.Payload.TaskId,
                    item.UserId);
            }
        }
    }

    private static FollowUpSnapshot? FindCurrentFollowUp(IReadOnlyCollection<TaskActivity> activities)
    {
        var lifecycleAtUtc = activities
            .Where(activity => IsLifecycleAction(activity.Action))
            .Select(activity => (DateTime?)activity.CreatedAtUtc)
            .Max();

        var management = activities
            .Where(activity => IsManagementAction(activity.Action))
            .Where(activity => !lifecycleAtUtc.HasValue || activity.CreatedAtUtc > lifecycleAtUtc.Value)
            .OrderByDescending(activity => activity.CreatedAtUtc)
            .ThenByDescending(activity => activity.Id)
            .FirstOrDefault();

        if (management is null || management.Action != WebsiteWorkAttentionActionService.FollowUpAssignedAction)
        {
            return null;
        }

        var details = ParseDetails(management.DetailsJson);
        var ownerUserId = ReadGuid(details, "followUpOwnerUserId");
        var ownerEmail = ReadString(details, "followUpOwnerEmail");
        var dueAtUtc = ReadDateTime(details, "followUpDueAtUtc");
        return ownerUserId.HasValue && !string.IsNullOrWhiteSpace(ownerEmail) && dueAtUtc.HasValue
            ? new FollowUpSnapshot(
                ownerUserId.Value,
                ownerEmail,
                ReadString(details, "followUpOwnerName"),
                dueAtUtc.Value)
            : null;
    }

    private static bool IsRelevantAction(string action)
        => action == WebsiteWorkAttentionActionService.FollowUpAssignedAction ||
           action == WebsiteWorkAttentionActionService.FollowUpResolvedAction ||
           action == WebsiteWorkAttentionActionService.AcknowledgedAction ||
           action == WebsiteWorkAttentionActionService.SnoozedAction ||
           IsLifecycleAction(action);

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

    private sealed record FollowUpSnapshot(
        Guid OwnerUserId,
        string OwnerEmail,
        string? OwnerName,
        DateTime DueAtUtc);
}
