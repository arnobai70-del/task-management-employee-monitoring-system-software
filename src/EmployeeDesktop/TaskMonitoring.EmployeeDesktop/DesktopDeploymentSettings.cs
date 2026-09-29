using System.IO;
using System.Text.Json;

namespace TaskMonitoring.EmployeeDesktop;

internal sealed class DesktopDeploymentSettings
{
    public string? ServerUrl { get; set; }
    public bool LockServerUrl { get; set; }

    public static DesktopDeploymentSettings? Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "desktop-settings.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<DesktopDeploymentSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
            if (settings is null || string.IsNullOrWhiteSpace(settings.ServerUrl))
            {
                return null;
            }

            if (!Uri.TryCreate(settings.ServerUrl.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return null;
            }

            settings.ServerUrl = uri.AbsoluteUri.TrimEnd('/');
            return settings;
        }
        catch
        {
            return null;
        }
    }
}
