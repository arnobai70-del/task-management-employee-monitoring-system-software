using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TaskMonitoring.EmployeeService;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var serverUrl = builder.Configuration["EmployeeService:ServerUrl"]?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttps && serverUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("EmployeeService:ServerUrl must be an absolute HTTP/HTTPS URL.");
        }

        var allowInsecureHttp = builder.Configuration.GetValue("EmployeeService:AllowInsecureHttp", false);
        if (serverUri.Scheme != Uri.UriSchemeHttps && !allowInsecureHttp)
        {
            throw new InvalidOperationException("EmployeeService:ServerUrl must use HTTPS unless EmployeeService:AllowInsecureHttp is explicitly enabled for development.");
        }

        var heartbeatSeconds = builder.Configuration.GetValue("EmployeeService:HeartbeatSeconds", 60);
        if (heartbeatSeconds is < 15 or > 3600)
        {
            throw new InvalidOperationException("EmployeeService:HeartbeatSeconds must be between 15 and 3600 seconds.");
        }

        builder.Services.AddWindowsService(options => options.ServiceName = "TaskMonitoringEmployeeService");
        builder.Services.AddHostedService<Worker>();
        builder.Build().Run();
    }
}
