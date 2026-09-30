using Microsoft.AspNetCore.SignalR;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Hubs;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkRealtimePublisher
{
    Task PublishCompletionAsync(WebsiteWorkCompletionResponse completion, CancellationToken cancellationToken);

    Task PublishSubmissionAsync(WebsiteWorkSubmissionResponse submission, CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task PublishFollowUpAsync(Guid userId, WebsiteWorkFollowUpRealtimeResponse followUp, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class SignalRWebsiteWorkRealtimePublisher(IHubContext<RealtimeHub> hubContext) : IWebsiteWorkRealtimePublisher
{
    public Task PublishCompletionAsync(WebsiteWorkCompletionResponse completion, CancellationToken cancellationToken)
        => hubContext.Clients.Group(RealtimeGroups.TaskManagers)
            .SendAsync("websiteWorkCompleted", completion, cancellationToken);

    public Task PublishSubmissionAsync(WebsiteWorkSubmissionResponse submission, CancellationToken cancellationToken)
        => hubContext.Clients.Group(RealtimeGroups.TaskManagers)
            .SendAsync("websiteWorkSubmitted", submission, cancellationToken);

    public Task PublishFollowUpAsync(Guid userId, WebsiteWorkFollowUpRealtimeResponse followUp, CancellationToken cancellationToken)
        => hubContext.Clients.Group(RealtimeGroups.User(userId))
            .SendAsync("websiteWorkFollowUpChanged", followUp, cancellationToken);
}
