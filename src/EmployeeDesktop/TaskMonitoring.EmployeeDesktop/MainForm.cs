namespace TaskMonitoring.EmployeeDesktop;

public sealed class MainForm : Form
{
    private readonly EmployeeApiClient _apiClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _heartbeatTimer;

    private readonly Panel _loginPanel = new() { Dock = DockStyle.Fill };
    private readonly Panel _dashboardPanel = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly TextBox _emailTextBox = new() { Dock = DockStyle.Fill, PlaceholderText = "employee@company.com" };
    private readonly TextBox _passwordTextBox = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Button _loginButton = new() { Text = "Sign in", AutoSize = true };
    private readonly Label _loginStatusLabel = new() { AutoSize = true };

    private readonly Label _profileLabel = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 11, FontStyle.Bold) };
    private readonly Label _serverLabel = new() { AutoSize = true };
    private readonly Label _attendanceLabel = new() { AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold) };
    private readonly Label _heartbeatLabel = new() { AutoSize = true };
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _logoutButton = new() { Text = "Sign out", AutoSize = true };
    private readonly Button _checkInButton = new() { Text = "Check in", AutoSize = true };
    private readonly Button _startBreakButton = new() { Text = "Start break", AutoSize = true };
    private readonly Button _endBreakButton = new() { Text = "End break", AutoSize = true };
    private readonly Button _checkOutButton = new() { Text = "Check out", AutoSize = true };
    private readonly CheckBox _autoStartCheckBox = new() { Text = "Start desktop app with Windows", AutoSize = true };
    private readonly DataGridView _tasksGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly TextBox _accessTextBox = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9)
    };
    private readonly TextBox _privacyTextBox = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical
    };

    private EmployeeDesktopDashboard? _dashboard;
    private bool _heartbeatBusy;
    private bool _suppressAutoStartChange;

    public MainForm(EmployeeApiClient apiClient)
    {
        _apiClient = apiClient;
        Text = "Task Monitoring - Employee Desktop";
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLoginPanel();
        BuildDashboardPanel();
        Controls.Add(_dashboardPanel);
        Controls.Add(_loginPanel);

        _heartbeatTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _heartbeatTimer.Tick += HeartbeatTimerOnTick;

        _loginButton.Click += LoginButtonOnClick;
        _refreshButton.Click += RefreshButtonOnClick;
        _logoutButton.Click += LogoutButtonOnClick;
        _checkInButton.Click += async (_, _) => await RunAttendanceActionAsync(_apiClient.CheckInAsync);
        _startBreakButton.Click += async (_, _) => await RunAttendanceActionAsync(_apiClient.StartBreakAsync);
        _endBreakButton.Click += async (_, _) => await RunAttendanceActionAsync(_apiClient.EndBreakAsync);
        _checkOutButton.Click += async (_, _) => await RunAttendanceActionAsync(_apiClient.CheckOutAsync);
        _autoStartCheckBox.CheckedChanged += AutoStartCheckBoxOnCheckedChanged;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _serverLabel.Text = $"Server: {_apiClient.ServerBaseUri}";
        _loginStatusLabel.Text = "Checking saved session...";
        _loginButton.Enabled = false;

        try
        {
            if (await _apiClient.TryRestoreSessionAsync(_lifetime.Token))
            {
                await ShowDashboardAsync();
            }
            else
            {
                ShowLogin("Sign in with your employee account.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowLogin($"Unable to restore the saved session: {exception.Message}");
        }
        finally
        {
            _loginButton.Enabled = true;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _heartbeatTimer.Stop();
        _heartbeatTimer.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnFormClosed(e);
    }

    private void BuildLoginPanel()
    {
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(80)
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "Employee Desktop",
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 20)
        };
        container.Controls.Add(title, 0, 0);
        container.SetColumnSpan(title, 2);

        container.Controls.Add(new Label { Text = "Email", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        container.Controls.Add(_emailTextBox, 1, 1);
        container.Controls.Add(new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        container.Controls.Add(_passwordTextBox, 1, 2);

        var buttonFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttonFlow.Controls.Add(_loginButton);
        buttonFlow.Controls.Add(_loginStatusLabel);
        container.Controls.Add(buttonFlow, 1, 3);

        var privacyNote = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = "This desktop client is visible and transparent. It does not capture keystrokes, clipboard contents, microphone/camera, hidden screenshots, browser history, unrelated private files, or application-window contents."
        };
        container.Controls.Add(privacyNote, 0, 4);
        container.SetColumnSpan(privacyNote, 2);

        _serverLabel.Margin = new Padding(0, 12, 0, 0);
        container.Controls.Add(_serverLabel, 0, 5);
        container.SetColumnSpan(_serverLabel, 2);
        _loginPanel.Controls.Add(container);
        AcceptButton = _loginButton;
    }

    private void BuildDashboardPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(12)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var identityPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        identityPanel.Controls.Add(_profileLabel);
        identityPanel.Controls.Add(_heartbeatLabel);
        identityPanel.Controls.Add(_autoStartCheckBox);
        header.Controls.Add(identityPanel, 0, 0);

        var headerButtons = new FlowLayoutPanel { AutoSize = true };
        headerButtons.Controls.Add(_refreshButton);
        headerButtons.Controls.Add(_logoutButton);
        header.Controls.Add(headerButtons, 1, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildTodayTab());
        tabs.TabPages.Add(BuildTasksTab());
        tabs.TabPages.Add(BuildAccessTab());
        tabs.TabPages.Add(BuildPrivacyTab());

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(tabs, 0, 1);
        _dashboardPanel.Controls.Add(layout);
    }

    private TabPage BuildTodayTab()
    {
        var page = new TabPage("Today");
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(20),
            AutoScroll = true
        };
        _attendanceLabel.Margin = new Padding(0, 0, 0, 16);
        layout.Controls.Add(_attendanceLabel);

        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.Add(_checkInButton);
        actions.Controls.Add(_startBreakButton);
        actions.Controls.Add(_endBreakButton);
        actions.Controls.Add(_checkOutButton);
        layout.Controls.Add(actions);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildTasksTab()
    {
        var page = new TabPage("My Tasks");
        page.Controls.Add(_tasksGrid);
        return page;
    }

    private TabPage BuildAccessTab()
    {
        var page = new TabPage("My Access");
        page.Controls.Add(_accessTextBox);
        return page;
    }

    private TabPage BuildPrivacyTab()
    {
        var page = new TabPage("Privacy & Status");
        page.Controls.Add(_privacyTextBox);
        return page;
    }

    private async void LoginButtonOnClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_emailTextBox.Text) || string.IsNullOrEmpty(_passwordTextBox.Text))
        {
            _loginStatusLabel.Text = "Email and password are required.";
            return;
        }

        _loginButton.Enabled = false;
        _loginStatusLabel.Text = "Signing in...";
        try
        {
            await _apiClient.LoginAsync(_emailTextBox.Text, _passwordTextBox.Text, _lifetime.Token);
            _passwordTextBox.Clear();
            await ShowDashboardAsync();
        }
        catch (ApiClientException exception)
        {
            _loginStatusLabel.Text = exception.Message;
        }
        catch (HttpRequestException exception)
        {
            _loginStatusLabel.Text = $"Cannot reach server: {exception.Message}";
        }
        finally
        {
            _loginButton.Enabled = true;
        }
    }

    private async void RefreshButtonOnClick(object? sender, EventArgs e)
    {
        await RefreshDashboardAsync(showErrorDialog: true);
    }

    private async void LogoutButtonOnClick(object? sender, EventArgs e)
    {
        _heartbeatTimer.Stop();
        SetDashboardBusy(true);
        try
        {
            await _apiClient.LogoutAsync(_lifetime.Token);
        }
        catch (ApiClientException)
        {
            // Local credentials are cleared even when the server rejects the request.
        }
        catch (HttpRequestException)
        {
            // Local credentials are cleared even when the server cannot be reached.
        }
        finally
        {
            _dashboard = null;
            SetDashboardBusy(false);
            ShowLogin("Signed out. Local session token cleared.");
        }
    }

    private async Task ShowDashboardAsync()
    {
        _loginPanel.Visible = false;
        _dashboardPanel.Visible = true;
        AcceptButton = null;
        _suppressAutoStartChange = true;
        try
        {
            _autoStartCheckBox.Checked = AutoStartManager.IsEnabled();
        }
        finally
        {
            _suppressAutoStartChange = false;
        }

        await RefreshDashboardAsync(showErrorDialog: true);
        await SendHeartbeatAsync();
        _heartbeatTimer.Start();
    }

    private void ShowLogin(string status)
    {
        _heartbeatTimer.Stop();
        _dashboardPanel.Visible = false;
        _loginPanel.Visible = true;
        _loginStatusLabel.Text = status;
        AcceptButton = _loginButton;
        _emailTextBox.Focus();
    }

    private async Task RefreshDashboardAsync(bool showErrorDialog)
    {
        SetDashboardBusy(true);
        try
        {
            _dashboard = await _apiClient.GetDashboardAsync(_lifetime.Token);
            RenderDashboard(_dashboard);
        }
        catch (ApiClientException exception)
        {
            if (showErrorDialog)
            {
                MessageBox.Show(exception.Message, "Unable to refresh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (HttpRequestException exception)
        {
            if (showErrorDialog)
            {
                MessageBox.Show($"Cannot reach server: {exception.Message}", "Connection error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            SetDashboardBusy(false);
        }
    }

    private void RenderDashboard(EmployeeDesktopDashboard dashboard)
    {
        _profileLabel.Text = $"{dashboard.Profile.FullName} ({dashboard.Profile.EmployeeCode}) - {dashboard.Profile.JobTitle}";
        _attendanceLabel.Text = BuildAttendanceText(dashboard.Attendance);
        UpdateAttendanceButtons(dashboard.Attendance.State);

        _tasksGrid.DataSource = dashboard.Tasks
            .OrderBy(task => task.DueDate ?? DateOnly.MaxValue)
            .ThenBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
            .Select(task => new
            {
                Project = task.ProjectCode,
                task.Title,
                task.Status,
                task.Priority,
                Due = task.DueDate?.ToString("yyyy-MM-dd") ?? "-"
            })
            .ToList();

        var accessLines = new List<string>();
        accessLines.Add("RDP ASSIGNMENTS");
        foreach (var item in dashboard.RdpAssignments)
        {
            accessLines.Add($"- {item.Name}: {item.Host}:{item.Port} | User ref: {item.UsernameReference ?? "-"} | Credential ref: {item.CredentialReference ?? "-"} | Expires: {item.ExpiresOn?.ToString("yyyy-MM-dd") ?? "-"}");
        }

        accessLines.Add(string.Empty);
        accessLines.Add("IP ASSIGNMENTS");
        foreach (var item in dashboard.IpAssignments)
        {
            accessLines.Add($"- {item.IpAddress} | Device: {item.DeviceName} | MAC: {item.MacAddress ?? "-"} | {item.Status}");
        }

        accessLines.Add(string.Empty);
        accessLines.Add("WEBSITE ASSIGNMENTS");
        foreach (var item in dashboard.WebsiteAssignments)
        {
            accessLines.Add($"- {item.Name}: {item.Url} | User ref: {item.UsernameReference ?? "-"} | Access: {item.AccessLevel} | Expires: {item.ExpiresOn?.ToString("yyyy-MM-dd") ?? "-"}");
        }
        _accessTextBox.Lines = accessLines.ToArray();

        var disclosure = dashboard.MonitoringDisclosure;
        var privacyLines = new List<string>
        {
            "Purpose",
            disclosure.Purpose,
            string.Empty,
            "Data this client/server flow uses"
        };
        privacyLines.AddRange(disclosure.CollectedFields.Select(value => $"- {value}"));
        privacyLines.Add(string.Empty);
        privacyLines.Add("Data this client does NOT collect");
        privacyLines.AddRange(disclosure.NotCollectedFields.Select(value => $"- {value}"));
        privacyLines.Add(string.Empty);
        privacyLines.Add($"Server: {_apiClient.ServerBaseUri}");
        privacyLines.Add("Heartbeat is visible here and contains only client version, platform, authenticated identity and server-derived attendance state.");
        _privacyTextBox.Lines = privacyLines.ToArray();
    }

    private static string BuildAttendanceText(AttendanceState attendance)
    {
        if (attendance.Session is null)
        {
            return $"Attendance: {attendance.State}";
        }

        var session = attendance.Session;
        return $"Attendance: {attendance.State} | Work date: {session.WorkDate:yyyy-MM-dd} | Started: {session.StartedAtUtc.ToLocalTime():g} | Late: {session.LateMinutes} min | Breaks: {session.TotalBreakMinutes} min";
    }

    private void UpdateAttendanceButtons(string state)
    {
        var normalized = state.Trim();
        _checkInButton.Enabled = string.Equals(normalized, "NotCheckedIn", StringComparison.OrdinalIgnoreCase);
        _startBreakButton.Enabled = string.Equals(normalized, "Working", StringComparison.OrdinalIgnoreCase);
        _endBreakButton.Enabled = string.Equals(normalized, "OnBreak", StringComparison.OrdinalIgnoreCase);
        _checkOutButton.Enabled = string.Equals(normalized, "Working", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunAttendanceActionAsync(Func<CancellationToken, Task> action)
    {
        SetDashboardBusy(true);
        try
        {
            await action(_lifetime.Token);
            await RefreshDashboardAsync(showErrorDialog: true);
            await SendHeartbeatAsync();
        }
        catch (ApiClientException exception)
        {
            MessageBox.Show(exception.Message, "Attendance action failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (HttpRequestException exception)
        {
            MessageBox.Show($"Cannot reach server: {exception.Message}", "Connection error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetDashboardBusy(false);
        }
    }

    private async void HeartbeatTimerOnTick(object? sender, EventArgs e)
    {
        await SendHeartbeatAsync();
    }

    private async Task SendHeartbeatAsync()
    {
        if (_heartbeatBusy || !_apiClient.IsAuthenticated)
        {
            return;
        }

        _heartbeatBusy = true;
        try
        {
            var response = await _apiClient.SendHeartbeatAsync(_lifetime.Token);
            _heartbeatLabel.Text = $"Connected - last heartbeat {response.LastSeenAtUtc.ToLocalTime():T} - {response.AttendanceState}";
            var intervalSeconds = Math.Clamp(response.RecommendedIntervalSeconds, 30, 300);
            _heartbeatTimer.Interval = checked(intervalSeconds * 1000);
        }
        catch (ApiClientException)
        {
            _heartbeatLabel.Text = $"Connection issue - {DateTime.Now:T}. Attendance actions require the server.";
        }
        catch (HttpRequestException)
        {
            _heartbeatLabel.Text = $"Connection issue - {DateTime.Now:T}. Attendance actions require the server.";
        }
        finally
        {
            _heartbeatBusy = false;
        }
    }

    private void AutoStartCheckBoxOnCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressAutoStartChange)
        {
            return;
        }

        try
        {
            AutoStartManager.SetEnabled(_autoStartCheckBox.Checked);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            _suppressAutoStartChange = true;
            try
            {
                _autoStartCheckBox.Checked = AutoStartManager.IsEnabled();
            }
            finally
            {
                _suppressAutoStartChange = false;
            }

            MessageBox.Show(exception.Message, "Startup setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetDashboardBusy(bool busy)
    {
        _refreshButton.Enabled = !busy;
        _logoutButton.Enabled = !busy;
        if (busy)
        {
            _checkInButton.Enabled = false;
            _startBreakButton.Enabled = false;
            _endBreakButton.Enabled = false;
            _checkOutButton.Enabled = false;
        }
        else if (_dashboard is not null)
        {
            UpdateAttendanceButtons(_dashboard.Attendance.State);
        }
    }
}
