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

    public SetupForm()
    {
        Text = "TaskMonitoring Employee Setup";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(620, 430);
        MinimumSize = new Size(620, 430);
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

        installButton.Click += InstallButton_Click;
        backButton.Click += (_, _) => ShowWelcome();
        cancelButton.Click += (_, _) => Close();
    }

    private void BuildWelcomePage()
    {
        var title = new Label
        {
            Text = "TaskMonitoring Employee Workspace",
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(42, 48)
        };
        var description = new Label
        {
            Text = "This wizard installs the employee Desktop, visible Windows Service, automatic updater, shortcuts and rollback support for your organization.\n\nThe installer verifies the signed release package and the organization's pinned publisher certificate before activation.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Location = new Point(46, 112)
        };
        var next = new Button { Text = "Next", Width = 110, Height = 34, Location = new Point(452, 340) };
        var cancel = new Button { Text = "Cancel", Width = 90, Height = 34, Location = new Point(350, 340) };
        next.Click += (_, _) => ShowInstall();
        cancel.Click += (_, _) => Close();
        welcomePanel.Controls.AddRange([title, description, cancel, next]);
    }

    private void BuildInstallPage()
    {
        var title = new Label
        {
            Text = "Ready to install",
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(38, 30)
        };
        var explanation = new Label
        {
            Text = "Confirm the organization endpoints and publisher identity, then choose Install.",
            AutoSize = true,
            Location = new Point(41, 72)
        };

        AddField("Server URL", serverUrlText, 112);
        AddField("Update manifest URL", updateManifestText, 177);
        AddField("Publisher certificate SHA-256", publisherText, 242);

        statusLabel.Location = new Point(42, 305);
        installButton.Location = new Point(452, 350);
        backButton.Location = new Point(250, 350);
        cancelButton.Location = new Point(350, 350);

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

        if (!IsHttpsUrl(serverUrl))
        {
            ShowError("Server URL must be an absolute HTTPS URL.");
            return;
        }
        if (!IsHttpsUrl(updateManifestUrl))
        {
            ShowError("Update manifest URL must be an absolute HTTPS URL.");
            return;
        }
        if (!Regex.IsMatch(publisher, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant))
        {
            ShowError("Publisher certificate SHA-256 must contain exactly 64 hexadecimal characters.");
            return;
        }

        var releaseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
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
            ShowError("This setup executable must remain inside the complete signed TaskMonitoring release bundle.");
            return;
        }

        SetBusy(true, "Verifying publisher signatures and installing components...");
        try
        {
            var verificationAndInstall = @"
$ErrorActionPreference = 'Stop'
$expected = $env:TM_PUBLISHER_SHA256
$files = $env:TM_SIGNED_SCRIPTS -split [IO.Path]::PathSeparator
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
& $env:TM_INSTALLER -ReleaseDirectory $env:TM_RELEASE_DIRECTORY -ServerUrl $env:TM_SERVER_URL -UpdateManifestUrl $env:TM_UPDATE_MANIFEST_URL -PublisherCertificateSha256 $expected
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
"@;

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
            startInfo.Environment["TM_PUBLISHER_SHA256"] = publisher;
            startInfo.Environment["TM_SIGNED_SCRIPTS"] = string.Join(Path.PathSeparator, signedScripts);
            startInfo.Environment["TM_INSTALLER"] = installer;
            startInfo.Environment["TM_RELEASE_DIRECTORY"] = releaseDirectory;
            startInfo.Environment["TM_SERVER_URL"] = serverUrl;
            startInfo.Environment["TM_UPDATE_MANIFEST_URL"] = updateManifestUrl;

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the signed installer.");
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
                "TaskMonitoring Employee Workspace was installed successfully. Use the Desktop or Start Menu shortcut to sign in.",
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
            if (!IsDisposed)
            {
                installButton.Enabled = true;
                backButton.Enabled = true;
                cancelButton.Enabled = true;
                UseWaitCursor = false;
            }
        }
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

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(uri.Host);

    private static string NormalizeFingerprint(string value) =>
        Regex.Replace(value ?? string.Empty, "[^0-9A-Fa-f]", string.Empty).ToUpperInvariant();

    private static string GetMetadata(string key) =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value?.Trim() ?? string.Empty;
}
