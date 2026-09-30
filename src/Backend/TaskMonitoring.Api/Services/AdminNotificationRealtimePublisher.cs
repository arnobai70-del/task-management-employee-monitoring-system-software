using Microsoft.AspNetCore.SignalR;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Hubs;

namespace TaskMonitoring.Api.Services;

public interface IAdminNotificationRealtimePublisher
{
    Task PublishAsync(
        Guid userId,
        AdminNotificationResponse notification,
        CancellationToken cancellationToken);
}

public sealed class SignalRAdminNotificationRealtimePublisher(
    IHubContext<RealtimeHub> hubContext) : IAdminNotificationRealtimePublisher
{
    public Task PublishAsync(
        Guid userId,
        AdminNotificationResponse notification,
        CancellationToken cancellationToken)
        => hubContext.Clients.Group(RealtimeGroups.User(userId))
            .SendAsync("adminNotificationCreated", notification, cancellationToken);
}
