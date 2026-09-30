using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace TaskMonitoring.Api.Infrastructure;

public sealed class AdminNotificationRealtimeInterceptor(
    IAdminNotificationRealtimePublisher realtimePublisher,
    ILogger<AdminNotificationRealtimeInterceptor> logger) : SaveChangesInterceptor
{
    private readonly List<(Guid UserId, AdminNotificationResponse Notification)> _pending = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Queue(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await PublishAsync(cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Queue(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<TaskActivity>()
                     .Where(entry =>
                         entry.State == EntityState.Added &&
                         entry.Entity.Action == AdminNotificationService.NotificationAction))
        {
            if (!entry.Entity.ActorUserId.HasValue)
            {
                continue;
            }

            var notification = AdminNotificationService.ToResponse(entry.Entity);
            if (notification is not null)
            {
                _pending.Add((entry.Entity.ActorUserId.Value, notification));
            }
        }
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
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
                await realtimePublisher.PublishAsync(item.UserId, item.Notification, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Admin notification {NotificationId} was saved but realtime delivery to user {UserId} failed.",
                    item.Notification.Id,
                    item.UserId);
            }
        }
    }
}
