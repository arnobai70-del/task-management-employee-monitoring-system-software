using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Infrastructure;

public sealed class SurveyNotificationInterceptor(
    TimeProvider timeProvider,
    IRealtimeEventPublisher eventPublisher,
    ILogger<SurveyNotificationInterceptor> logger) : SaveChangesInterceptor
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

        var entries = context.ChangeTracker.Entries<WebsiteAssignment>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified)
            .ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.AccessLevel == WebsiteAccessLevel.Survey)
                {
                    Queue(
                        context,
                        entry.Entity.EmployeeId,
                        EmployeeNotificationKind.SurveyAssigned,
                        "Survey assigned",
                        SurveyMessage(entry.Entity, "A survey website was assigned to you."),
                        entry.Entity.Id,
                        now);
                }
                continue;
            }

            var accessLevel = entry.Property(x => x.AccessLevel);
            var wasSurvey = accessLevel.OriginalValue == WebsiteAccessLevel.Survey;
            var isSurvey = accessLevel.CurrentValue == WebsiteAccessLevel.Survey;
            if (!wasSurvey && !isSurvey)
            {
                continue;
            }

            var employeeProperty = entry.Property(x => x.EmployeeId);
            if (wasSurvey && !isSurvey)
            {
                Queue(
                    context,
                    employeeProperty.OriginalValue,
                    EmployeeNotificationKind.SurveyUnassigned,
                    "Survey assignment removed",
                    SurveyMessage(entry.Entity, "This survey is no longer assigned to you."),
                    entry.Entity.Id,
                    now);
                continue;
            }

            if (!wasSurvey && isSurvey)
            {
                Queue(
                    context,
                    employeeProperty.CurrentValue,
                    EmployeeNotificationKind.SurveyAssigned,
                    "Survey assigned",
                    SurveyMessage(entry.Entity, "A survey website was assigned to you."),
                    entry.Entity.Id,
                    now);
                continue;
            }

            if (employeeProperty.IsModified && employeeProperty.OriginalValue != employeeProperty.CurrentValue)
            {
                Queue(
                    context,
                    employeeProperty.OriginalValue,
                    EmployeeNotificationKind.SurveyUnassigned,
                    "Survey reassigned",
                    SurveyMessage(entry.Entity, "This survey is no longer assigned to you."),
                    entry.Entity.Id,
                    now);
                Queue(
                    context,
                    employeeProperty.CurrentValue,
                    EmployeeNotificationKind.SurveyAssigned,
                    "Survey assigned",
                    SurveyMessage(entry.Entity, "This survey is now assigned to you."),
                    entry.Entity.Id,
                    now);
                continue;
            }

            if (HasMeaningfulSurveyUpdate(entry))
            {
                Queue(
                    context,
                    entry.Entity.EmployeeId,
                    EmployeeNotificationKind.SurveyUpdated,
                    "Survey assignment updated",
                    SurveyMessage(entry.Entity, SurveyUpdatePrefix(entry)),
                    entry.Entity.Id,
                    now);
            }
        }
    }

    private void Queue(
        DbContext context,
        Guid employeeId,
        EmployeeNotificationKind kind,
        string title,
        string message,
        Guid surveyLinkId,
        DateTime now)
    {
        var notification = new EmployeeNotification
        {
            EmployeeId = employeeId,
            Kind = kind,
            Title = title,
            Message = message,
            EntityType = "SurveyLink",
            EntityId = surveyLinkId,
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
                logger.LogWarning(ex, "Durable survey notification {NotificationId} was saved but realtime delivery failed.", notification.Id);
            }
        }
    }

    private static bool HasMeaningfulSurveyUpdate(EntityEntry<WebsiteAssignment> entry)
        => entry.Property(x => x.Name).IsModified
           || entry.Property(x => x.Url).IsModified
           || entry.Property(x => x.StartsOn).IsModified
           || entry.Property(x => x.ExpiresOn).IsModified
           || entry.Property(x => x.IsActive).IsModified
           || entry.Property(x => x.Notes).IsModified;

    private static string SurveyUpdatePrefix(EntityEntry<WebsiteAssignment> entry)
    {
        if (entry.Property(x => x.IsActive).IsModified)
        {
            return entry.Entity.IsActive
                ? "This survey assignment is active again."
                : "This survey assignment was deactivated.";
        }

        return "Survey details were updated.";
    }

    private static string SurveyMessage(WebsiteAssignment assignment, string prefix)
    {
        var title = assignment.Name.Trim();
        if (title.Length > 160)
        {
            title = title[..160] + "…";
        }
        return $"{prefix} {title}";
    }
}
