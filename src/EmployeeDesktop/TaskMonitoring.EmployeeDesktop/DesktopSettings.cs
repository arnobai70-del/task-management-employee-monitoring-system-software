using System.Text.Json;
using TaskMonitoring.Employee.Shared;

namespace TaskMonitoring.EmployeeDesktop;

public sealed record DesktopSettings(Uri ServerBaseUri)
{
    public static DesktopSettings Load()
    {
        var environmentUrl = Environment.GetEnvironmentVariable("TASK_MONITORING_SERVER_URL");
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            return new DesktopSettings(ServerEndpointPolicy.ParseAndValidate(environmentUrl));
        }

        var path = Path.Combine(AppContext.BaseDirectory, "desktopsettings.json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("desktopsettings.json was not found and TASK_MONITORING_SERVER_URL is not set.");
        }

        var json = File.ReadAllText(path);
        var payload = JsonSerializer.Deserialize<SettingsPayload>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("desktopsettings.json is invalid.");

        return new DesktopSettings(ServerEndpointPolicy.ParseAndValidate(payload.ServerBaseUrl));
    }

    private sealed record SettingsPayload(string? ServerBaseUrl);
}
