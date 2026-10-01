using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TaskMonitoring.EmployeeSetup;

public sealed class SetupForm : Form
{
    private readonly Panel welcomePanel = new() { Dock = DockStyle.Fill };
    private readonly Panel installPanel = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly TextBox serverUrlText = new() { Width = 430 };
    private readonly TextBox updateManifestText = new() { Width = 430 };
    private readonly TextBox publisherText = new() { Width = 430, CharacterCasing = CharacterCasing.Upper };
    private readonly Label statusLabel = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button installButton = new() { Text = "Install", Width = 110, Height = 34 };
    private readonly Button backButton = new() { Text = "Back", Width = 90, Height = 34 };
    private readonly Button cancelButton = new() { Text = "Cancel", Width = 90, Height = 34 };
    private readonly bool developmentMode;
    private ExtractedPayload? extractedPayload;

    public SetupForm()
    {
        developmentMode = string.Equals(GetMetadata("TaskMonitoring.DevelopmentSetup"), "true", StringComparison.OrdinalIgnoreCase);

        Text = developmentMode ? "TaskMonitoring Employee Setup - DEVELOPMENT TEST" : "TaskMonitoring Employee Setup";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 450);
        MinimumSize = new Size(620, 450);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Font = new Font("Segoe UI", 10F);

        BuildWelcomePage();
        BuildInstallPage();
        Controls.Add(installPanel);
        Controls.Add(welcomePanel);

        serverUrlText.Text = GetMetadata("TaskMonitoring.ServerUrl");
        updateManifestText.Text = GetMetadata("TaskMonitoring.UpdateManifestUrl");
        publisherText.Text = NormalizeFingerprint(GetMetadata("TaskMonitoring.PublisherSha256"));

        serverUrlText.ReadOnly = serverUrlText.TextLength > 0;
        updateManifestText.ReadOnly = updateManifestText.TextLength > 0;
        publisherText.ReadOnly = publisherText.TextLength > 0;
        publisherText.Enabled = !developmentMode;

        installButton.Click += InstallButton_Click;
        backButton.Click += (_, _) => ShowWelcome();
        cancelButton.Click += (_, _) => Close();
        FormClosed += (_, _) => CleanupPayload();
    }

    private void BuildWelcomePage()
    {
        var title = new Label
        {
            Text = "TaskMonitoring Employee Workspace",
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(42, 42)
        };
        var description = new Label
        {
            Text = "This wizard installs the employee Desktop, visible Windows Service, automatic updater, shortcuts and rollback support for your organization.\n\nThe production installer verifies the signed release package and the organization's pinned publisher certificate before activation.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Location = new Point(46, 104)
        };
        welcomePanel.Controls.AddRange([title, description]);

        if (developmentMode)
        {
            var warning = new Label
            {
                Text = "DEVELOPMENT TEST BUILD: unsigned packages and HTTP are allowed. Do not use this installer for production deployment.",
                ForeColor = Color.DarkRed,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Location = new Point(46, 242)
            };
            welcomePanel.Controls.Add(warning);
        }

        var next = new Button { Text = "Next", Width = 110, Height = 34, Location = new Point(452, 362) };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 34, Location = new Point(350, 362) };
        next.Click += (_, _) => ShowInstall();
        cancel.Click += (_, _) => Close();
        welcomePanel.Controls.AddRange([cancel, next]);
    }

    private void BuildInstallPage()
    {
        var title = new Label
        {
            Text = developmentMode ? "Ready to install development test" : "Ready to install",
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(38, 24)
        };
        var explanation = new Label
        {
            Text = developmentMode
                ? "Confirm the test server URL. Auto-update is disabled when no update manifest URL is supplied."
                : "Confirm the organization endpoints and publisher identity, then choose Install.",
            AutoSize = true,
            MaximumSize = new Size(530, 0),
            Location = new Point(41, 66)
        };

        AddField("Server URL", serverUrlText, 108);
        AddField("Update manifest URL", updateManifestText, 173);
        AddField("Publisher certificate SHA-256", publisherText, 238);

        statusLabel.Location = new Point(42, 303);
        installButton.Location = new Point(452, 370);
        backButton.Location = new Point(250, 370);
        cancelButton.Location = new Point(350, 370);

        installPanel.Controls.AddRange([title, explanation, statusLabel, installButton, backButton, cancelButton]);
    }

    private void AddField(string label, TextBox textBox, int y)
    {
        var fieldLabel = new Label { Text = label, AutoSize = true, Location = new Point(42, y) };
        textBox.Location = new Point(42, y + 24);
        installPanel.Controls.Add(fieldLabel);
        installPanel.Controls.Add(textBox);
    }

    private void ShowWelcome()
    {
        installPanel.Visible = false;
        welcomePanel.Visible = true;
        AcceptButton = null;
    }

    private void ShowInstall()
    {
        welcomePanel.Visible = false;
        installPanel.Visible = true;
        AcceptButton = installButton;
    }

    private async void InstallButton_Click(object? sender, EventArgs e)
    {
        statusLabel.Text = string.Empty;
        var serverUrl = serverUrlText.Text.Trim();
        var updateManifestUrl = updateManifestText.Text.Trim();
        var publisher = NormalizeFingerprint(publisherText.Text);

        if (!IsAllowedUrl(serverUrl, developmentMode))
        {
            ShowError(developmentMode
                ? "Server URL must be an absolute HTTP or HTTPS URL."
                : "Server URL must be an absolute HTTPS URL.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(updateManifestUrl) && !IsAllowedUrl(updateManifestUrl, developmentMode))
        {
            ShowError(developmentMode
                ? "Update manifest URL must be an absolute HTTP or HTTPS URL when supplied."
                : "Update manifest URL must be an absolute HTTPS URL.");
            return;
        }
        if (!developmentMode && string.IsNullOrWhiteSpace(updateManifestUrl))
        {
            ShowError("Production installation requires an HTTPS update manifest URL.");
            return;
        }
        if (!developmentMode && !Regex.IsMatch(publisher, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant))
        {
            ShowError("Publisher certificate SHA-256 must contain exactly 64 hexadecimal characters.");
            return;
        }

        string releaseDirectory;
        try
        {
            CleanupPayload();
            extractedPayload = SetupPayload.ExtractEmbeddedPayload();
            releaseDirectory = extractedPayload?.DirectoryPath ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            ShowError($"Could not unpack the installer payload. {ex.Message}");
            return;
        }

        var installer = Path.Combine(releaseDirectory, "install-employee-windows.ps1");
        var manifest = Path.Combine(releaseDirectory, "release.json");
        var signedScripts = new[]
        {
            installer,
            Path.Combine(releaseDirectory, "deployment-common.ps1"),
            Path.Combine(releaseDirectory, "uninstall-employee-windows.ps1"),
            Path.Combine(releaseDirectory, "rollback-employee-windows.ps1"),
            Path.Combine(releaseDirectory, "run-central-agent-update.ps1")
        };
        if (!File.Exists(manifest) || signedScripts.Any(path => !File.Exists(path)))
        {
            ShowError(SetupPayload.HasEmbeddedPayload
                ? "The embedded TaskMonitoring installer payload is incomplete."
                : "This setup executable must remain inside the complete TaskMonitoring release bundle.");
            CleanupPayload();
            return;
        }

        SetBusy(true, developmentMode ? "Installing development test components..." : "Verifying publisher signatures and installing components...");
        try
        {
            var verificationAndInstall = """
$ErrorActionPreference = 'Stop'
$development = $env:TM_DEVELOPMENT_SETUP -eq '1'
$expected = $env:TM_PUBLISHER_SHA256
$files = $env:TM_SIGNED_SCRIPTS -split [IO.Path]::PathSeparator
if (-not $development) {
    foreach ($file in $files) {
        $signature = Get-AuthenticodeSignature -FilePath $file
        if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.SignerCertificate) {
            throw "Authenticode validation failed for '$file'. Status=$($signature.Status)."
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $actual = (($sha.ComputeHash($signature.SignerCertificate.RawData) | ForEach-Object { $_.ToString('X2') }) -join '')
        }
        finally {
            $sha.Dispose()
        }
        if ($actual -ne $expected) {
            throw "Publisher certificate mismatch for '$file'."
        }
    }
}

$args = @{
    ReleaseDirectory = $env:TM_RELEASE_DIRECTORY
    ServerUrl = $env:TM_SERVER_URL
}
if ($development) {
    $args.AllowUnsignedDevelopmentBuild = $true
    $args.AllowHttpForDevelopment = $true
}
else {
    $args.PublisherCertificateSha256 = $expected
}
if ([string]::IsNullOrWhiteSpace($env:TM_UPDATE_MANIFEST_URL)) {
    $args.DisableAutoUpdate = $true
}
else {
    $args.UpdateManifestUrl = $env:TM_UPDATE_MANIFEST_URL
}
& $env:TM_INSTALLER @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
""";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(verificationAndInstall);
            startInfo.Environment["TM_DEVELOPMENT_SETUP"] = developmentMode ? "1" : "0";
            startInfo.Environment["TM_PUBLISHER_SHA256"] = publisher;
            startInfo.Environment["TM_SIGNED_SCRIPTS"] = string.Join(Path.PathSeparator, signedScripts);
            startInfo.Environment["TM_INSTALLER"] = installer;
            startInfo.Environment["TM_RELEASE_DIRECTORY"] = releaseDirectory;
            startInfo.Environment["TM_SERVER_URL"] = serverUrl;
            startInfo.Environment["TM_UPDATE_MANIFEST_URL"] = updateManifestUrl;

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the installer.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(error) ? output : error;
                detail = detail.Trim();
                if (detail.Length > 1200)
                {
                    detail = detail[^1200..];
                }
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? $"Installer exited with code {process.ExitCode}."
                    : detail);
            }

            statusLabel.ForeColor = Color.DarkGreen;
            statusLabel.Text = "Installation completed successfully.";
            MessageBox.Show(
                this,
                developmentMode
                    ? "TaskMonitoring Employee Workspace development test was installed successfully. Use the Desktop or Start Menu shortcut to sign in."
                    : "TaskMonitoring Employee Workspace was installed successfully. Use the Desktop or Start Menu shortcut to sign in.",
                "Installation complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            CleanupPayload();
            if (!IsDisposed)
            {
                installButton.Enabled = true;
                backButton.Enabled = true;
                cancelButton.Enabled = true;
                UseWaitCursor = false;
            }
        }
    }

    private void CleanupPayload()
    {
        extractedPayload?.Dispose();
        extractedPayload = null;
    }

    private void SetBusy(bool busy, string message)
    {
        installButton.Enabled = !busy;
        backButton.Enabled = !busy;
        cancelButton.Enabled = !busy;
        UseWaitCursor = busy;
        statusLabel.ForeColor = SystemColors.ControlText;
        statusLabel.Text = message;
    }

    private void ShowError(string message)
    {
        statusLabel.ForeColor = Color.DarkRed;
        statusLabel.Text = message;
    }

    private static bool IsAllowedUrl(string value, bool allowHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               (allowHttp && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeFingerprint(string value) =>
        Regex.Replace(value ?? string.Empty, "[^0-9A-Fa-f]", string.Empty).ToUpperInvariant();

    private static string GetMetadata(string key) =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value?.Trim() ?? string.Empty;
}
