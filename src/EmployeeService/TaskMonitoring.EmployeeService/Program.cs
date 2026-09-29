using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TaskMonitoring.EmployeeService;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "TaskMonitoringEmployeeService");
        builder.Services.AddHostedService<Worker>();
        builder.Build().Run();
    }
}
