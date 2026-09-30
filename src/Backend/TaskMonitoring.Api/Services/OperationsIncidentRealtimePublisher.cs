using Microsoft.AspNetCore.SignalR;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Hubs;

namespace TaskMonitoring.Api.Services;

public interface IOperationsIncidentRealtimePublisher
{
    Task PublishAsync(OperationsIncidentChangedResponse incident, CancellationToken cancellationToken);
}

public sealed class SignalROperationsIncidentRealtimePublisher(
    IHubContext<RealtimeHub> hubContext) : IOperationsIncidentRealtimePublisher
{
    public Task PublishAsync(OperationsIncidentChangedResponse incident, CancellationToken cancellationToken)
        => hubContext.Clients.Group(RealtimeGroups.OperationsReaders)
            .SendAsync("operationsIncidentChanged", incident, cancellationToken);
}
