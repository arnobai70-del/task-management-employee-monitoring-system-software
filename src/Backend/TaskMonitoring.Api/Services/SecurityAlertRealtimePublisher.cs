using Microsoft.AspNetCore.SignalR;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Hubs;

namespace TaskMonitoring.Api.Services;

public interface ISecurityAlertRealtimePublisher
{
    Task PublishAsync(SecurityAlertChangedResponse alert, CancellationToken cancellationToken);
}

public sealed class SignalRSecurityAlertRealtimePublisher(
    IHubContext<RealtimeHub> hubContext) : ISecurityAlertRealtimePublisher
{
    public Task PublishAsync(SecurityAlertChangedResponse alert, CancellationToken cancellationToken)
        => hubContext.Clients.Group(RealtimeGroups.SecurityAlertReaders)
            .SendAsync("securityAlertChanged", alert, cancellationToken);
}
