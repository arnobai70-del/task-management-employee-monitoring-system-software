using Microsoft.Extensions.Configuration;
using TaskMonitoring.Employee.Shared;

namespace TaskMonitoring.EmployeeAgent;

public sealed record AgentRuntimeOptions(Uri ServerBaseUri, TimeSpan HealthCheckInterval)
{
    public static AgentRuntimeOptions Load(IConfiguration configuration)
    {
        var serverUrl = Environment.GetEnvironmentVariable("TASK_MONITORING_SERVER_URL");
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            serverUrl = configuration["Agent:ServerBaseUrl"];
        }

        var intervalSeconds = 60;
        if (int.TryParse(configuration["Agent:HealthCheckIntervalSeconds"], out var configuredInterval))
        {
            intervalSeconds = Math.Clamp(configuredInterval, 15, 900);
        }

        return new AgentRuntimeOptions(
            ServerEndpointPolicy.ParseAndValidate(serverUrl),
            TimeSpan.FromSeconds(intervalSeconds));
    }
}
