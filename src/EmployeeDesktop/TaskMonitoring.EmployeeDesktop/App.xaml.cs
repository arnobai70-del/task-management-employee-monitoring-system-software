using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TaskMonitoring.EmployeeDesktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var deploymentSettings = DesktopDeploymentSettings.Load();

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Current.MainWindow is not Window mainWindow)
            {
                return;
            }

            var serverUrlBox = mainWindow.FindName("ServerUrlBox") as TextBox;

            EmployeeLoginExperience.Apply(mainWindow);

            if (deploymentSettings is null || serverUrlBox is null)
            {
                return;
            }

            serverUrlBox.Text = deploymentSettings.ServerUrl;
            serverUrlBox.IsReadOnly = deploymentSettings.LockServerUrl;
            serverUrlBox.ToolTip = deploymentSettings.LockServerUrl
                ? "Server address is managed by your organization."
                : null;
        });
    }
}
