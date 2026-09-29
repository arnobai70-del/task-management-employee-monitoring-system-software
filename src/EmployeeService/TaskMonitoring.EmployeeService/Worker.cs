using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaskMonitoring.EmployeeService;

public sealed class Worker(IConfiguration configuration, ILogger<Worker> logger) : BackgroundService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serverUrl = configuration["EmployeeService:ServerUrl"]!.Trim().TrimEnd('/');
        var intervalSeconds = configuration.GetValue("EmployeeService:HeartbeatSeconds", 60);
        var healthUri = new Uri(serverUrl + "/health/live", UriKind.Absolute);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckServerAsync(healthUri, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private async Task CheckServerAsync(Uri healthUri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            logger.LogInformation(
                "Employee service heartbeat. Machine={MachineName}; Server={Server}; Reachable={Reachable}; HttpStatus={StatusCode}",
                Environment.MachineName,
                healthUri.Host,
                response.IsSuccessStatusCode,
                (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Employee service heartbeat failed. Machine={MachineName}; Server={Server}; Reachable=false",
                Environment.MachineName,
                healthUri.Host);
        }
    }
}
