using System.Text;
using System.Windows;

namespace TaskMonitoring.EmployeeDesktop;

public partial class MainWindow : Window
{
    private readonly EmployeeApiClient _api = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        _api.Dispose();
        base.OnClosed(e);
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (!Uri.TryCreate(ServerUrlBox.Text.Trim(), UriKind.Absolute, out var serverUri) ||
                (serverUri.Scheme != Uri.UriSchemeHttps && serverUri.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException("Enter a valid HTTP/HTTPS server URL.");
            }

            if (string.IsNullOrWhiteSpace(EmailBox.Text) || string.IsNullOrWhiteSpace(PasswordBox.Password))
            {
                throw new InvalidOperationException("Email and password are required.");
            }

            _api.ConfigureServer(ServerUrlBox.Text);
            await _api.LoginAsync(EmailBox.Text.Trim(), PasswordBox.Password);
            PasswordBox.Clear();
            ConnectionStatusText.Text = $"Server: connected to {serverUri.Host}";
            MessageText.Text = "Signed in successfully.";
            await RefreshAttendanceAsync();
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RunAsync(RefreshAttendanceAsync);

    private async void CheckInButton_Click(object sender, RoutedEventArgs e)
        => await RunAttendanceActionAsync("check-in");

    private async void StartBreakButton_Click(object sender, RoutedEventArgs e)
        => await RunAttendanceActionAsync("breaks/start");

    private async void EndBreakButton_Click(object sender, RoutedEventArgs e)
        => await RunAttendanceActionAsync("breaks/end");

    private async void CheckOutButton_Click(object sender, RoutedEventArgs e)
        => await RunAttendanceActionAsync("check-out");

    private async Task RunAttendanceActionAsync(string action)
    {
        await RunAsync(async () =>
        {
            await _api.ExecuteAttendanceActionAsync(action);
            await RefreshAttendanceAsync();
            MessageText.Text = "Attendance updated.";
        });
    }

    private async Task RefreshAttendanceAsync()
    {
        var state = await _api.GetAttendanceStatusAsync();
        var text = new StringBuilder(state.StateLabel);
        if (state.Session is not null)
        {
            text.AppendLine();
            text.Append($"Shift: {state.Session.ShiftName} | Work date: {state.Session.WorkDate:yyyy-MM-dd}");
            text.AppendLine();
            text.Append($"Started: {state.Session.StartedAtUtc.ToLocalTime():g} | Break: {state.Session.TotalBreakMinutes} min | Late: {state.Session.LateMinutes} min");
            if (state.Session.EndedAtUtc is not null)
            {
                text.AppendLine();
                text.Append($"Ended: {state.Session.EndedAtUtc.Value.ToLocalTime():g}");
            }
        }

        AttendanceStatusText.Text = text.ToString();
    }

    private async Task RunAsync(Func<Task> operation)
    {
        LoginButton.IsEnabled = false;
        MessageText.Text = string.Empty;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            MessageText.Text = ex.Message;
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }
}
