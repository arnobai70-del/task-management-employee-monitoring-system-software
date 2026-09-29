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
        var serverUrl = configuration["EmployeeService:ServerUrl"]?.Trim().TrimEnd('/');
        var intervalSeconds = Math.Clamp(configuration.GetValue("EmployeeService:HeartbeatSeconds", 60), 15, 3600);

        Uri? serverUri = null;
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttps && serverUri.Scheme != Uri.UriSchemeHttp))
        {
            logger.LogWarning("Employee service is running, but EmployeeService:ServerUrl is not configured with a valid HTTP/HTTPS URL.");
            serverUri = null;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (serverUri is not null)
            {
                await CheckServerAsync(serverUri, stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private async Task CheckServerAsync(Uri serverUri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, serverUri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            logger.LogInformation(
                "Employee service heartbeat. Machine={MachineName}; Server={Server}; Reachable=true; HttpStatus={StatusCode}",
                Environment.MachineName,
                serverUri.Host,
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
                serverUri.Host);
        }
    }
}
