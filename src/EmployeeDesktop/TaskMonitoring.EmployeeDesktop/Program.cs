namespace TaskMonitoring.EmployeeDesktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            var settings = DesktopSettings.Load();
            var tokenStore = new SessionTokenStore();
            using var apiClient = new EmployeeApiClient(settings, tokenStore);
            Application.Run(new MainForm(apiClient));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            MessageBox.Show(
                exception.Message,
                "Task Monitoring - Configuration Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
