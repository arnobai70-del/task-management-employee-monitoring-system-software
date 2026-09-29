using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaskMonitoring.EmployeeAgent;

public sealed class ConnectivityWorker(AgentRuntimeOptions options, ILogger<ConnectivityWorker> logger) : BackgroundService
{
    private readonly HttpClient _httpClient = CreateHttpClient(options.ServerBaseUri);
    private bool? _lastReachable;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Task Monitoring Employee Agent started. It checks only backend reachability and does not receive employee credentials or collect user activity.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckConnectivityAsync(stoppingToken);

            try
            {
                await Task.Delay(options.HealthCheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CheckConnectivityAsync(CancellationToken cancellationToken)
    {
        bool reachable;
        string? reason = null;
        try
        {
            using var response = await _httpClient.GetAsync("health", cancellationToken);
            reachable = response.IsSuccessStatusCode;
            if (!reachable)
            {
                reason = $"HTTP {(int)response.StatusCode}";
            }
        }
        catch (HttpRequestException exception)
        {
            reachable = false;
            reason = exception.Message;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reachable = false;
            reason = "health request timed out";
        }

        if (_lastReachable == reachable)
        {
            return;
        }

        _lastReachable = reachable;
        if (reachable)
        {
            logger.LogInformation("Backend connectivity is healthy: {ServerUrl}", options.ServerBaseUri);
        }
        else
        {
            logger.LogWarning("Backend connectivity is unavailable: {ServerUrl}. Reason: {Reason}", options.ServerBaseUri, reason ?? "unknown");
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }

    private static HttpClient CreateHttpClient(Uri baseUri)
    {
        var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TaskMonitoring.EmployeeAgent/1.0");
        return client;
    }
}
