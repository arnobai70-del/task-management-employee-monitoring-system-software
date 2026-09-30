using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskMonitoring.EmployeeDesktop;

internal sealed partial class AgentHealthCollector
{
    private const string ServiceName = "TaskMonitoringEmployeeService";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AgentHealthReport> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var programDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TaskMonitoring");
        var state = await ReadInstallStateAsync(Path.Combine(programDataRoot, "install-state.json"), cancellationToken);
        var installRoot = ResolveInstallRoot();
        var servicePath = installRoot is null
            ? null
            : Path.Combine(installRoot, "Service", "TaskMonitoring.EmployeeService.exe");
        var updaterPath = Path.Combine(programDataRoot, "Updater", "TaskMonitoring.EmployeeUpdater.exe");

        return new AgentHealthReport(
            Environment.MachineName,
            CurrentVersion(),
            FileVersion(servicePath),
            FileVersion(updaterPath),
            await IsServiceRunningAsync(cancellationToken),
            NormalizeVersion(state?.Version),
            string.IsNullOrWhiteSpace(state?.Channel) ? null : state.Channel.Trim(),
            NormalizeUtc(state?.LastSuccessfulUpdateUtc),
            NormalizeUtc(state?.RolledBackAtUtc));
    }

    private static string? ResolveInstallRoot()
    {
        try
        {
            var desktopRoot = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return desktopRoot.Parent?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static string CurrentVersion()
        => NormalizeVersion(Assembly.GetEntryAssembly()?.GetName().Version?.ToString()) ?? "0.0.0";

    private static string? FileVersion(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            return NormalizeVersion(FileVersionInfo.GetVersionInfo(path).FileVersion);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Version.TryParse(value.Trim(), out var version))
        {
            return null;
        }

        return $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";
    }

    private static async Task<bool> IsServiceRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("sc.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("query");
            startInfo.ArgumentList.Add(ServiceName);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            return process.ExitCode == 0 && RunningStateRegex().IsMatch(output);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<InstallState?> ReadInstallStateAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<InstallState>(stream, JsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    [GeneratedRegex(@"(?m)^\s*[^:\r\n]+:\s*4(?:\s|$)")]
    private static partial Regex RunningStateRegex();

    private sealed class InstallState
    {
        public string? Version { get; set; }
        public string? Channel { get; set; }
        public DateTime? LastSuccessfulUpdateUtc { get; set; }
        public DateTime? RolledBackAtUtc { get; set; }
    }
}

public sealed record AgentHealthReport(
    string MachineName,
    string? DesktopVersion,
    string? ServiceVersion,
    string? UpdaterVersion,
    bool ServiceRunning,
    string? InstalledVersion,
    string? UpdateChannel,
    DateTime? LastSuccessfulUpdateAtUtc,
    DateTime? RolledBackAtUtc);
