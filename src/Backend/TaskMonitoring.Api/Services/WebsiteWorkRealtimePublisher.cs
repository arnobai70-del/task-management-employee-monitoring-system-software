using Microsoft.AspNetCore.SignalR;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Hubs;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkRealtimePublisher
{
    Task PublishCompletionAsync(WebsiteWorkCompletionResponse completion, CancellationToken cancellationToken);
}

public sealed class SignalRWebsiteWorkRealtimePublisher(IHubContext<RealtimeHub> hubContext) : IWebsiteWorkRealtimePublisher
{
    public Task PublishCompletionAsync(WebsiteWorkCompletionResponse completion, CancellationToken cancellationToken)
        => hubContext.Clients.Group(RealtimeGroups.TaskManagers)
            .SendAsync("websiteWorkCompleted", completion, cancellationToken);
}
