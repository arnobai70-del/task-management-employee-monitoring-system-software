using Microsoft.Extensions.Options;
using TaskMonitoring.Api.Configuration;

namespace TaskMonitoring.Api.Services;

public sealed class OperationsIncidentHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<OperationsOptions> options,
    ILogger<OperationsIncidentHostedService> logger) : BackgroundService
{
    private readonly OperationsOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ScanSafelyAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Clamp(_options.IncidentScanIntervalSeconds, 30, 3600)));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ScanSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ScanSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IOperationsIncidentService>();
            await service.ScanAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Operations incident scan failed. The next scheduled scan will retry.");
        }
    }
}
