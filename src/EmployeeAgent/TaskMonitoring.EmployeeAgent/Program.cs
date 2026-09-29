using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TaskMonitoring.EmployeeAgent;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "Task Monitoring Employee Agent";
        });

        var runtimeOptions = AgentRuntimeOptions.Load(builder.Configuration);
        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddHostedService<ConnectivityWorker>();

        await builder.Build().RunAsync();
    }
}
