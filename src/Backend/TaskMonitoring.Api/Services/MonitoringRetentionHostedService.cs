namespace TaskMonitoring.Api.Services;

public sealed class MonitoringRetentionHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<MonitoringRetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IMonitoringTelemetryService>();
                var deleted = await service.PurgeExpiredAsync(stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Purged {Count} expired monitoring activity segments.", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Monitoring telemetry retention purge failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
