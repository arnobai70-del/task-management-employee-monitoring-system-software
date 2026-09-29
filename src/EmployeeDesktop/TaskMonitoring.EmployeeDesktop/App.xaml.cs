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
        if (deploymentSettings is null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Current.MainWindow?.FindName("ServerUrlBox") is not TextBox serverUrlBox)
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
