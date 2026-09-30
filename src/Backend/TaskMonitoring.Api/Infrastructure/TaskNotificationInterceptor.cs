using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Infrastructure;

public sealed class TaskNotificationInterceptor(
    TimeProvider timeProvider,
    IRealtimeEventPublisher eventPublisher,
    ILogger<TaskNotificationInterceptor> logger) : SaveChangesInterceptor
{
    private readonly List<EmployeeNotification> _pendingPublications = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        QueueNotifications(eventData.Context);
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

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _pendingPublications.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void QueueNotifications(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var taskEntries = context.ChangeTracker.Entries<ProjectTask>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified)
            .ToArray();
        if (taskEntries.Length == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var entry in taskEntries)
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.AssigneeEmployeeId.HasValue)
                {
                    Queue(context, entry.Entity.AssigneeEmployeeId.Value, EmployeeNotificationKind.TaskAssigned,
                        "New task assigned", TaskMessage(entry.Entity, "A new task was assigned to you."), entry.Entity.Id, now);
                }
                continue;
            }

            var assigneeProperty = entry.Property(x => x.AssigneeEmployeeId);
            if (assigneeProperty.IsModified && assigneeProperty.OriginalValue != assigneeProperty.CurrentValue)
            {
                if (assigneeProperty.OriginalValue.HasValue)
                {
                    Queue(context, assigneeProperty.OriginalValue.Value, EmployeeNotificationKind.TaskUnassigned,
                        "Task unassigned", TaskMessage(entry.Entity, "You are no longer assigned to this task."), entry.Entity.Id, now);
                }

                if (assigneeProperty.CurrentValue.HasValue)
                {
                    Queue(context, assigneeProperty.CurrentValue.Value, EmployeeNotificationKind.TaskAssigned,
                        "Task assigned", TaskMessage(entry.Entity, "This task is now assigned to you."), entry.Entity.Id, now);
                }
                continue;
            }

            if (!entry.Entity.AssigneeEmployeeId.HasValue)
            {
                continue;
            }

            if (entry.Property(x => x.Status).IsModified)
            {
                var reviewActivity = FindWebsiteReviewActivity(context, entry.Entity.Id);
                if (reviewActivity is not null)
                {
                    var reviewMessage = WebsiteReviewMessage(entry.Entity, reviewActivity);
                    Queue(context, entry.Entity.AssigneeEmployeeId.Value, EmployeeNotificationKind.TaskStatusChanged,
                        reviewMessage.Title, reviewMessage.Message, entry.Entity.Id, now);
                }
                else
                {
                    Queue(context, entry.Entity.AssigneeEmployeeId.Value, EmployeeNotificationKind.TaskStatusChanged,
                        "Task status changed", TaskMessage(entry.Entity, $"Status is now {entry.Entity.Status}."), entry.Entity.Id, now);
                }
                continue;
            }

            if (HasMeaningfulTaskUpdate(entry))
            {
                Queue(context, entry.Entity.AssigneeEmployeeId.Value, EmployeeNotificationKind.TaskUpdated,
                    "Task updated", TaskMessage(entry.Entity, "Task details were updated."), entry.Entity.Id, now);
            }
        }
    }

    private static TaskActivity? FindWebsiteReviewActivity(DbContext context, Guid taskId)
        => context.ChangeTracker.Entries<TaskActivity>()
            .Where(x => x.State == EntityState.Added && x.Entity.ProjectTaskId == taskId)
            .Select(x => x.Entity)
            .LastOrDefault(x =>
                x.Action == WebsiteWorkService.CompletedAction ||
                x.Action == WebsiteWorkReviewService.ApprovedAction ||
                x.Action == WebsiteWorkReviewService.ReopenedAction);

    private static (string Title, string Message) WebsiteReviewMessage(ProjectTask task, TaskActivity activity)
    {
        if (activity.Action == WebsiteWorkService.CompletedAction)
        {
            return ("Completion submitted", TaskMessage(task, "Your completion is waiting for manager review."));
        }

        if (activity.Action == WebsiteWorkReviewService.ApprovedAction)
        {
            return ("Website work approved", TaskMessage(task, "Your completion was approved."));
        }

        var comment = ReadComment(activity.DetailsJson);
        if (string.IsNullOrWhiteSpace(comment))
        {
            return ("Correction requested", TaskMessage(task, "Your manager reopened this work for correction."));
        }

        if (comment.Length > 500)
        {
            comment = comment[..500] + "…";
        }
        return ("Correction requested", TaskMessage(task, $"Your manager requested changes: {comment}"));
    }

    private static string? ReadComment(string detailsJson)
    {
        try
        {
            using var json = JsonDocument.Parse(detailsJson);
            return json.RootElement.TryGetProperty("comment", out var comment) && comment.ValueKind == JsonValueKind.String
                ? comment.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Queue(
        DbContext context,
        Guid employeeId,
        EmployeeNotificationKind kind,
        string title,
        string message,
        Guid taskId,
        DateTime now)
    {
        var notification = new EmployeeNotification
        {
            EmployeeId = employeeId,
            Kind = kind,
            Title = title,
            Message = message,
            EntityType = "ProjectTask",
            EntityId = taskId,
            CreatedAtUtc = now
        };
        context.Set<EmployeeNotification>().Add(notification);
        _pendingPublications.Add(notification);
    }

    private async Task PublishQueuedAsync(CancellationToken cancellationToken)
    {
        if (_pendingPublications.Count == 0)
        {
            return;
        }

        var notifications = _pendingPublications.ToArray();
        _pendingPublications.Clear();
        foreach (var notification in notifications)
        {
            try
            {
                await eventPublisher.PublishNotificationAsync(
                    notification.EmployeeId,
                    RealtimeWorkspaceService.ToNotificationResponse(notification),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Durable notification {NotificationId} was saved but realtime delivery failed.", notification.Id);
            }
        }
    }

    private static bool HasMeaningfulTaskUpdate(EntityEntry<ProjectTask> entry)
        => entry.Property(x => x.Title).IsModified
           || entry.Property(x => x.Description).IsModified
           || entry.Property(x => x.Priority).IsModified
           || entry.Property(x => x.DueDate).IsModified;

    private static string TaskMessage(ProjectTask task, string prefix)
    {
        var title = task.Title.Trim();
        if (title.Length > 160)
        {
            title = title[..160] + "…";
        }
        return $"{prefix} {title}";
    }
}
