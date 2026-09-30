using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace TaskMonitoring.EmployeeDesktop;

public partial class MainWindow : Window
{
    private readonly EmployeeApiClient _api = new();
    private readonly EmployeeRealtimeClient _realtime;
    private readonly ForegroundActivityCollector _activityCollector = new();
    private readonly DispatcherTimer _presenceTimer;
    private readonly DispatcherTimer _monitoringTimer;
    private EmployeeMonitoringPolicyResponse? _monitoringPolicy;
    private DateTime _monitoringPolicyRefreshAtUtc;
    private bool _monitoringTickRunning;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _realtime = new EmployeeRealtimeClient(_api);
        _realtime.NotificationReceived += Realtime_NotificationReceived;
        _realtime.ConnectionChanged += Realtime_ConnectionChanged;
        _presenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _presenceTimer.Tick += PresenceTimer_Tick;
        _monitoringTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _monitoringTimer.Tick += MonitoringTimer_Tick;
    }

    protected override void OnClosed(EventArgs e)
    {
        _presenceTimer.Stop();
        _monitoringTimer.Stop();
        _api.Dispose();
        base.OnClosed(e);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_api.IsAuthenticated)
        {
            return;
        }

        e.Cancel = true;
        _presenceTimer.Stop();
        _monitoringTimer.Stop();
        try
        {
            await _realtime.StopAsync();
            await _api.LogoutAsync();
        }
        catch
        {
        }
        finally
        {
            _allowClose = true;
            Close();
        }
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
            LogoutButton.IsEnabled = true;
            MessageText.Text = "Signed in successfully.";
            await RefreshWorkspaceAsync();
            await RefreshMonitoringPolicyAsync();
            await SendPresenceHeartbeatAsync();
            await _realtime.StartAsync();
            _presenceTimer.Start();
            _monitoringTimer.Start();
        });
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            _presenceTimer.Stop();
            _monitoringTimer.Stop();
            await _realtime.StopAsync();
            await _api.LogoutAsync();
            ClearWorkspace();
            ConnectionStatusText.Text = "Server: signed out";
            LogoutButton.IsEnabled = false;
            MessageText.Text = "Signed out and refresh token revoked.";
        });
    }

    private async void PresenceTimer_Tick(object? sender, EventArgs e)
    {
        if (!_api.IsAuthenticated)
        {
            _presenceTimer.Stop();
            return;
        }

        try
        {
            await SendPresenceHeartbeatAsync();
            await RefreshNotificationsAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatusText.Text = $"Server: presence sync delayed ({ex.Message})";
        }
    }

    private async void MonitoringTimer_Tick(object? sender, EventArgs e)
    {
        if (_monitoringTickRunning)
        {
            return;
        }
        if (!_api.IsAuthenticated)
        {
            _monitoringTimer.Stop();
            return;
        }

        _monitoringTickRunning = true;
        try
        {
            if (_monitoringPolicy is null || DateTime.UtcNow >= _monitoringPolicyRefreshAtUtc)
            {
                await RefreshMonitoringPolicyAsync();
            }

            var policy = _monitoringPolicy;
            if (policy is null || !policy.IsEnabled)
            {
                return;
            }

            var activity = _activityCollector.TryCapture(policy);
            if (activity is not null)
            {
                await _api.RecordApplicationActivityAsync(activity.ProcessName, activity.WindowTitle);
            }
        }
        catch (Exception ex)
        {
            MonitoringStatusText.Text = $"Approved activity sync delayed: {ex.Message}";
        }
        finally
        {
            _monitoringTickRunning = false;
        }
    }

    private void Realtime_NotificationReceived(object? sender, EmployeeNotificationResponse notification)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            MessageText.Text = $"New notification: {notification.Title}";
            await RefreshNotificationsAsync();
            await RefreshTasksAsync();
            await RefreshSurveysAsync();
        });
    }

    private void Realtime_ConnectionChanged(object? sender, bool connected)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            RealtimeStatusText.Text = connected ? "Notifications: realtime connected" : "Notifications: reconnecting";
        });
    }

    private async void RefreshAttendanceButton_Click(object sender, RoutedEventArgs e)
        => await RunAsync(RefreshAttendanceAsync);

    private async void RefreshTasksButton_Click(object sender, RoutedEventArgs e)
        => await RunAsync(RefreshTasksAsync);

    private async void RefreshSurveysButton_Click(object sender, RoutedEventArgs e)
        => await RunAsync(RefreshSurveysAsync);

    private async void RefreshAccessButton_Click(object sender, RoutedEventArgs e)
        => await RunAsync(RefreshAccessAsync);

    private async void RefreshNotificationsButton_Click(object sender, RoutedEventArgs e)
        => await RunAsync(RefreshNotificationsAsync);

    private async void RefreshMonitoringPolicyButton_Click(object sender, RoutedEventArgs e)
        => await RunAsync(RefreshMonitoringPolicyAsync);

    private async void MarkNotificationReadButton_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationsGrid.SelectedItem is not EmployeeNotificationResponse notification)
        {
            MessageText.Text = "Select a notification first.";
            return;
        }

        await RunAsync(async () =>
        {
            await _api.MarkNotificationReadAsync(notification.Id);
            await RefreshNotificationsAsync();
            MessageText.Text = "Notification marked as read.";
        });
    }

    private async void OpenSurveyButton_Click(object sender, RoutedEventArgs e)
    {
        if (SurveyGrid.SelectedItem is not ExternalSurveyAssignmentResponse survey)
        {
            MessageText.Text = "Select a survey assignment first.";
            return;
        }

        await RunAsync(async () =>
        {
            if (!survey.IsActive)
            {
                throw new InvalidOperationException("The selected survey assignment is inactive.");
            }

            var confirmed = await _api.RecordSurveyLinkOpenAsync(survey.Id);
            if (!Uri.TryCreate(confirmed.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("The selected survey URL is invalid.");
            }

            Process.Start(new ProcessStartInfo { FileName = confirmed.Url, UseShellExecute = true });
            MessageText.Text = $"Opened survey website: {confirmed.Title}. Complete the survey on the external website.";
            await RefreshSurveysAsync();
        });
    }

    private async void OpenWebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (WebsiteGrid.SelectedItem is not WebsiteAssignmentResponse website)
        {
            MessageText.Text = "Select a company website assignment first.";
            return;
        }

        await RunAsync(async () =>
        {
            if (!website.IsActive)
            {
                throw new InvalidOperationException("The selected website assignment is inactive.");
            }
            if (!Uri.TryCreate(website.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("The selected website URL is invalid.");
            }

            var result = await _api.RecordBusinessDomainActivityAsync(uri.IdnHost.ToLowerInvariant());
            Process.Start(new ProcessStartInfo { FileName = website.Url, UseShellExecute = true });
            MessageText.Text = result.Accepted
                ? $"Opened {website.Name}; approved business hostname activity recorded."
                : $"Opened {website.Name}; monitoring record not stored ({result.Reason ?? "not accepted"}).";
        });
    }

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
            await SendPresenceHeartbeatAsync();
            MessageText.Text = "Attendance updated.";
        });
    }

    private async Task RefreshWorkspaceAsync()
    {
        await RefreshAttendanceAsync();
        await RefreshTasksAsync();
        await RefreshSurveysAsync();
        await RefreshAccessAsync();
        await RefreshNotificationsAsync();
    }

    private async Task RefreshMonitoringPolicyAsync()
    {
        var policy = await _api.GetMonitoringPolicyAsync();
        _monitoringPolicy = policy;
        _monitoringPolicyRefreshAtUtc = DateTime.UtcNow.AddMinutes(5);
        _monitoringTimer.Interval = TimeSpan.FromSeconds(policy.IsEnabled ? Math.Clamp(policy.SampleIntervalSeconds, 15, 300) : 60);
        MonitoringStatusText.Text = policy.IsEnabled
            ? $"Approved activity monitoring: ON · {policy.Applications.Count} app rule(s) · {policy.BusinessDomains.Count} business domain(s) · retention {policy.RetentionDays} day(s)"
            : "Approved activity monitoring: OFF";
        MonitoringDisclosureText.Text = policy.DisclosureText;
    }

    private async Task SendPresenceHeartbeatAsync()
    {
        var presence = await _api.RecordPresenceHeartbeatAsync();
        ConnectionStatusText.Text = $"Server: connected · Presence: {presence.WorkState} · synced {DateTime.Now:t}";
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

    private async Task RefreshTasksAsync()
    {
        var result = await _api.GetMyTasksAsync(IncludeClosedTasksBox.IsChecked == true);
        TasksGrid.ItemsSource = result.Items;
    }

    private async Task RefreshSurveysAsync()
    {
        var result = await _api.GetMySurveyLinksAsync(IncludeInactiveSurveysBox.IsChecked == true);
        SurveyGrid.ItemsSource = result;
        SurveyCountText.Text = $"{result.Count} survey assignment(s)";
    }

    private async Task RefreshAccessAsync()
    {
        var result = await _api.GetMyAccessAsync(IncludeInactiveAccessBox.IsChecked == true);
        RdpGrid.ItemsSource = result.RdpAssignments;
        IpGrid.ItemsSource = result.IpAssignments;
        WebsiteGrid.ItemsSource = result.WebsiteAssignments;
    }

    private async Task RefreshNotificationsAsync()
    {
        var result = await _api.GetMyNotificationsAsync(UnreadNotificationsOnlyBox.IsChecked == true);
        NotificationsGrid.ItemsSource = result.Items;
        NotificationsCountText.Text = $"{result.TotalCount} notification(s)";
    }

    private void ClearWorkspace()
    {
        AttendanceStatusText.Text = "Sign in to load your attendance status.";
        TasksGrid.ItemsSource = null;
        SurveyGrid.ItemsSource = null;
        SurveyCountText.Text = "0 survey assignment(s)";
        RdpGrid.ItemsSource = null;
        IpGrid.ItemsSource = null;
        WebsiteGrid.ItemsSource = null;
        NotificationsGrid.ItemsSource = null;
        NotificationsCountText.Text = "0 notifications";
        RealtimeStatusText.Text = "Notifications: disconnected";
        MonitoringStatusText.Text = "Approved activity monitoring: sign in to load policy";
        MonitoringDisclosureText.Text = "Monitoring disclosure is loaded from the company server after sign-in.";
        _monitoringPolicy = null;
        _monitoringPolicyRefreshAtUtc = default;
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
