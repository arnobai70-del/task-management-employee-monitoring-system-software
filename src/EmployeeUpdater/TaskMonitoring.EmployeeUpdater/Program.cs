using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskMonitoring.EmployeeUpdater;

internal static partial class Program
{
    private const string DefaultServiceName = "TaskMonitoringEmployeeService";
    private const string DesktopProcessName = "TaskMonitoring.EmployeeDesktop";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        var settingsPath = GetArgument(args, "--settings") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TaskMonitoring",
            "update-settings.json");
        var force = args.Any(x => string.Equals(x, "--force", StringComparison.OrdinalIgnoreCase));
        var dryRun = args.Any(x => string.Equals(x, "--dry-run", StringComparison.OrdinalIgnoreCase));

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TaskMonitoring",
            "logs",
            "updater.log");
        using var logger = new FileLogger(logPath);

        try
        {
            using var mutex = new Mutex(initiallyOwned: false, "Global\\TaskMonitoringEmployeeUpdater");
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                logger.Write("Another updater process is already running; exiting.");
                return 0;
            }

            try
            {
                return RunAsync(settingsPath, force, dryRun, logger).GetAwaiter().GetResult();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            logger.Write($"Update failed: {ex}");
            return 20;
        }
    }

    private static async Task<int> RunAsync(string settingsPath, bool force, bool dryRun, FileLogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Employee updater runs only on Windows.");
        }

        if (!File.Exists(settingsPath))
        {
            throw new InvalidOperationException($"Update settings were not found: {settingsPath}");
        }

        var settings = JsonSerializer.Deserialize<UpdateSettings>(await File.ReadAllTextAsync(settingsPath), JsonOptions)
                       ?? throw new InvalidOperationException("Update settings are invalid JSON.");
        ValidateSettings(settings);

        if (!dryRun && !IsAdministrator())
        {
            throw new InvalidOperationException("Updater requires an elevated/SYSTEM process for installation changes.");
        }

        logger.Write($"Checking update channel '{settings.Channel}' from {settings.ManifestUrl}.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TaskMonitoring.EmployeeUpdater/1.0");
        using var manifestResponse = await http.GetAsync(settings.ManifestUrl, HttpCompletionOption.ResponseContentRead);
        manifestResponse.EnsureSuccessStatusCode();
        var manifestJson = await manifestResponse.Content.ReadAsStringAsync();
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestJson, JsonOptions)
                       ?? throw new InvalidOperationException("Release manifest is empty or invalid.");
        ValidateManifest(manifest, settings);

        var availableVersion = ParseVersion(manifest.Version, "release version");
        var minimumUpdaterVersion = ParseVersion(manifest.MinimumUpdaterVersion, "minimum updater version");
        var updaterVersion = NormalizeAssemblyVersion(typeof(Program).Assembly.GetName().Version);
        if (minimumUpdaterVersion > updaterVersion)
        {
            logger.Write($"Update {availableVersion} requires updater {minimumUpdaterVersion}, current updater is {updaterVersion}. Manual maintenance is required.");
            return 30;
        }

        var statePath = string.IsNullOrWhiteSpace(settings.StatePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TaskMonitoring", "install-state.json")
            : settings.StatePath;
        var installedState = await ReadInstallStateAsync(statePath);
        var installedVersion = ParseVersion(installedState.Version ?? "0.0.0", "installed version");

        if (availableVersion <= installedVersion)
        {
            logger.Write($"No update required. Installed={installedVersion}; Available={availableVersion}.");
            return 0;
        }

        var manifestUri = new Uri(settings.ManifestUrl, UriKind.Absolute);
        var packageUri = ResolvePackageUri(manifestUri, manifest.Package, settings.AllowInsecureHttp);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"TaskMonitoring-update-{Guid.NewGuid():N}");
        var packagePath = Path.Combine(tempRoot, manifest.Package.File);
        var stageRoot = Path.Combine(tempRoot, "stage");
        Directory.CreateDirectory(tempRoot);

        try
        {
            logger.Write($"Downloading update package {packageUri}.");
            using (var packageResponse = await http.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead))
            {
                packageResponse.EnsureSuccessStatusCode();
                await using var source = await packageResponse.Content.ReadAsStreamAsync();
                await using var destination = File.Create(packagePath);
                await source.CopyToAsync(destination);
            }

            if (new FileInfo(packagePath).Length != manifest.Package.SizeBytes)
            {
                throw new InvalidOperationException("Downloaded package size does not match the release manifest.");
            }

            VerifySha256(packagePath, manifest.Package.Sha256);
            Directory.CreateDirectory(stageRoot);
            ZipFile.ExtractToDirectory(packagePath, stageRoot, overwriteFiles: false);

            var stagedDesktop = Path.Combine(stageRoot, "desktop");
            var stagedService = Path.Combine(stageRoot, "service");
            var desktopExe = Path.Combine(stagedDesktop, "TaskMonitoring.EmployeeDesktop.exe");
            var serviceExe = Path.Combine(stagedService, "TaskMonitoring.EmployeeService.exe");
            EnsureFile(desktopExe);
            EnsureFile(serviceExe);
            VerifyFileVersion(desktopExe, availableVersion);
            VerifyFileVersion(serviceExe, availableVersion);

            if (settings.RequireSignedPackages)
            {
                VerifyPublisherSignature(desktopExe, settings.PublisherCertificateSha256!);
                VerifyPublisherSignature(serviceExe, settings.PublisherCertificateSha256!);
            }

            if (dryRun)
            {
                logger.Write($"Dry-run validation succeeded for {availableVersion}. No installation changes were made.");
                return 0;
            }

            if (!force && Process.GetProcessesByName(DesktopProcessName).Length > 0)
            {
                logger.Write("Employee Desktop is currently running. Update deferred until a later scheduled check.");
                return 10;
            }

            ApplyUpdate(settings, statePath, installedState, manifest, stageRoot, logger);
            logger.Write($"Updated successfully from {installedVersion} to {availableVersion}.");
            return 0;
        }
        finally
        {
            TryDeleteDirectory(tempRoot, logger);
        }
    }

    private static void ApplyUpdate(
        UpdateSettings settings,
        string statePath,
        InstallState installedState,
        ReleaseManifest manifest,
        string stageRoot,
        FileLogger logger)
    {
        var installRoot = Path.GetFullPath(settings.InstallRoot);
        var desktopRoot = Path.Combine(installRoot, "Desktop");
        var serviceRoot = Path.Combine(installRoot, "Service");
        var stagedDesktop = Path.Combine(stageRoot, "desktop");
        var stagedService = Path.Combine(stageRoot, "service");
        var backupRootBase = string.IsNullOrWhiteSpace(settings.BackupRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TaskMonitoring", "backups")
            : settings.BackupRoot;
        var backupRoot = Path.Combine(
            backupRootBase,
            $"{installedState.Version ?? "unknown"}-{DateTime.UtcNow:yyyyMMddHHmmss}");
        var backupDesktop = Path.Combine(backupRoot, "Desktop");
        var backupService = Path.Combine(backupRoot, "Service");

        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(backupRoot);

        var desktopConfig = ReadOptionalFile(Path.Combine(desktopRoot, "desktop-settings.json"));
        var serviceConfig = ReadOptionalFile(Path.Combine(serviceRoot, "appsettings.json"));
        var serviceName = string.IsNullOrWhiteSpace(settings.ServiceName) ? DefaultServiceName : settings.ServiceName;

        StopService(serviceName, logger);
        var replacementStarted = false;
        try
        {
            if (Directory.Exists(desktopRoot))
            {
                MoveDirectory(desktopRoot, backupDesktop);
            }
            if (Directory.Exists(serviceRoot))
            {
                MoveDirectory(serviceRoot, backupService);
            }

            MoveDirectory(stagedDesktop, desktopRoot);
            MoveDirectory(stagedService, serviceRoot);
            replacementStarted = true;

            WriteOptionalFile(Path.Combine(desktopRoot, "desktop-settings.json"), desktopConfig);
            WriteOptionalFile(Path.Combine(serviceRoot, "appsettings.json"), serviceConfig);

            StartService(serviceName, logger);

            var newState = new InstallState
            {
                Version = manifest.Version,
                Channel = manifest.Channel,
                InstalledAtUtc = DateTime.UtcNow,
                LastSuccessfulUpdateUtc = DateTime.UtcNow
            };
            WriteJson(statePath, newState);
            CleanupBackups(backupRootBase, Math.Clamp(settings.BackupRetention, 1, 5), logger);
        }
        catch
        {
            logger.Write("Update activation failed. Attempting rollback.");
            try
            {
                StopService(serviceName, logger);
            }
            catch (Exception stopEx)
            {
                logger.Write($"Rollback service stop warning: {stopEx.Message}");
            }

            if (replacementStarted)
            {
                TryDeleteDirectory(desktopRoot, logger);
                TryDeleteDirectory(serviceRoot, logger);
            }

            if (Directory.Exists(backupDesktop))
            {
                MoveDirectory(backupDesktop, desktopRoot);
            }
            if (Directory.Exists(backupService))
            {
                MoveDirectory(backupService, serviceRoot);
            }

            try
            {
                StartService(serviceName, logger);
            }
            catch (Exception startEx)
            {
                logger.Write($"Rollback restored files but service restart failed: {startEx.Message}");
            }

            throw;
        }
    }

    private static void ValidateSettings(UpdateSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ManifestUrl) ||
            !Uri.TryCreate(settings.ManifestUrl, UriKind.Absolute, out var manifestUri))
        {
            throw new InvalidOperationException("ManifestUrl must be an absolute URL.");
        }

        if (manifestUri.Scheme != Uri.UriSchemeHttps && !(settings.AllowInsecureHttp && manifestUri.Scheme == Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("ManifestUrl must use HTTPS unless AllowInsecureHttp is explicitly enabled for development.");
        }

        if (string.IsNullOrWhiteSpace(settings.Channel) || string.IsNullOrWhiteSpace(settings.InstallRoot))
        {
            throw new InvalidOperationException("Channel and InstallRoot are required.");
        }

        if (settings.RequireSignedPackages)
        {
            var normalized = NormalizeFingerprint(settings.PublisherCertificateSha256);
            if (normalized.Length != 64)
            {
                throw new InvalidOperationException("PublisherCertificateSha256 must contain 64 hexadecimal characters when signed packages are required.");
            }
        }
    }

    private static void ValidateManifest(ReleaseManifest manifest, UpdateSettings settings)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidOperationException($"Unsupported release manifest schema version {manifest.SchemaVersion}.");
        }
        if (!string.Equals(manifest.Channel, settings.Channel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Manifest channel '{manifest.Channel}' does not match configured channel '{settings.Channel}'.");
        }
        _ = ParseVersion(manifest.Version, "release version");
        _ = ParseVersion(manifest.MinimumUpdaterVersion, "minimum updater version");
        if (manifest.Package is null || string.IsNullOrWhiteSpace(manifest.Package.File) || string.IsNullOrWhiteSpace(manifest.Package.Sha256))
        {
            throw new InvalidOperationException("Release package metadata is incomplete.");
        }
        if (!string.Equals(Path.GetFileName(manifest.Package.File), manifest.Package.File, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Release package.file must be a simple file name without directory segments.");
        }
        if (manifest.Package.SizeBytes <= 0)
        {
            throw new InvalidOperationException("Release package size must be greater than zero.");
        }
    }

    private static Uri ResolvePackageUri(Uri manifestUri, ReleasePackage package, bool allowInsecureHttp)
    {
        var uri = string.IsNullOrWhiteSpace(package.Url)
            ? new Uri(manifestUri, package.File)
            : new Uri(package.Url, UriKind.Absolute);

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Package URL must use HTTPS unless AllowInsecureHttp is explicitly enabled for development.");
        }

        return uri;
    }

    private static async Task<InstallState> ReadInstallStateAsync(string path)
    {
        if (!File.Exists(path))
        {
            return new InstallState { Version = "0.0.0" };
        }

        return JsonSerializer.Deserialize<InstallState>(await File.ReadAllTextAsync(path), JsonOptions)
               ?? new InstallState { Version = "0.0.0" };
    }

    private static Version ParseVersion(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !StrictVersionRegex().IsMatch(value))
        {
            throw new InvalidOperationException($"{field} must use MAJOR.MINOR.PATCH numeric semantic version format.");
        }

        return Version.Parse(value);
    }

    private static Version NormalizeAssemblyVersion(Version? value)
        => value is null ? new Version(0, 0, 0) : new Version(value.Major, value.Minor, Math.Max(0, value.Build));

    private static void VerifyFileVersion(string path, Version expectedVersion)
    {
        var fileVersionText = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (string.IsNullOrWhiteSpace(fileVersionText) || !Version.TryParse(fileVersionText, out var fileVersion))
        {
            throw new InvalidOperationException($"Executable version metadata is missing or invalid for '{path}'.");
        }

        var actualVersion = new Version(fileVersion.Major, fileVersion.Minor, Math.Max(0, fileVersion.Build));
        if (actualVersion != expectedVersion)
        {
            throw new InvalidOperationException($"Executable version mismatch for '{path}'. Expected {expectedVersion}; actual {actualVersion}.");
        }
    }

    private static void VerifySha256(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        var expected = NormalizeFingerprint(expectedHash);
        if (expected.Length != 64 || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expected)))
        {
            throw new InvalidOperationException("Downloaded package SHA-256 verification failed.");
        }
    }

    private static void VerifyPublisherSignature(string path, string expectedFingerprint)
    {
        var script = """
            $ErrorActionPreference = 'Stop'
            $signature = Get-AuthenticodeSignature -LiteralPath $env:TM_VERIFY_FILE
            if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) { exit 41 }
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $actual = (($sha.ComputeHash($signature.SignerCertificate.RawData) | ForEach-Object { $_.ToString('X2') }) -join '')
            } finally { $sha.Dispose() }
            $expected = ($env:TM_VERIFY_CERT -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
            if ($actual -ne $expected) { exit 42 }
            exit 0
            """;

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
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
        startInfo.ArgumentList.Add(script);
        startInfo.Environment["TM_VERIFY_FILE"] = Path.GetFullPath(path);
        startInfo.Environment["TM_VERIFY_CERT"] = NormalizeFingerprint(expectedFingerprint);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Authenticode verifier.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"Authenticode verification failed for '{path}' (code {process.ExitCode}). {error}".Trim());
        }
    }

    private static string NormalizeFingerprint(string? value)
        => Regex.Replace(value ?? string.Empty, "[^0-9A-Fa-f]", string.Empty).ToUpperInvariant();

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void StopService(string serviceName, FileLogger logger)
    {
        if (!ServiceExists(serviceName))
        {
            throw new InvalidOperationException($"Service '{serviceName}' is not installed.");
        }
        if (ServiceState(serviceName).Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        logger.Write($"Stopping service {serviceName}.");
        _ = RunSc("stop", serviceName);
        WaitForServiceState(serviceName, "STOPPED", TimeSpan.FromSeconds(30));
    }

    private static void StartService(string serviceName, FileLogger logger)
    {
        logger.Write($"Starting service {serviceName}.");
        var result = RunSc("start", serviceName);
        if (result.ExitCode != 0 && !result.Output.Contains("already running", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not start service '{serviceName}'. {result.Output}");
        }
        WaitForServiceState(serviceName, "RUNNING", TimeSpan.FromSeconds(30));
    }

    private static bool ServiceExists(string serviceName)
        => RunSc("query", serviceName).ExitCode == 0;

    private static string ServiceState(string serviceName)
        => RunSc("query", serviceName).Output;

    private static void WaitForServiceState(string serviceName, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ServiceState(serviceName).Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException($"Service '{serviceName}' did not reach state '{expected}' within {timeout.TotalSeconds:0} seconds.");
    }

    private static (int ExitCode, string Output) RunSc(string command, string serviceName)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(serviceName);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start sc.exe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, string.Concat(stdout, Environment.NewLine, stderr).Trim());
    }

    private static byte[]? ReadOptionalFile(string path)
        => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static void WriteOptionalFile(string path, byte[]? content)
    {
        if (content is not null)
        {
            File.WriteAllBytes(path, content);
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    }

    private static void CleanupBackups(string root, int retention, FileLogger logger)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in new DirectoryInfo(root)
                     .EnumerateDirectories()
                     .OrderByDescending(x => x.CreationTimeUtc)
                     .Skip(retention))
        {
            TryDeleteDirectory(directory.FullName, logger);
        }
    }

    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            if (Directory.Exists(destination))
            {
                throw;
            }
        }

        CopyDirectory(source, destination);
        Directory.Delete(source, recursive: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists)
        {
            throw new DirectoryNotFoundException($"Directory was not found: {source}");
        }

        Directory.CreateDirectory(destination);
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(destination, file.Name), overwrite: false);
        }
        foreach (var directory in sourceInfo.EnumerateDirectories())
        {
            CopyDirectory(directory.FullName, Path.Combine(destination, directory.Name));
        }
    }

    private static void EnsureFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Expected update file was not found: {path}");
        }
    }

    private static void TryDeleteDirectory(string path, FileLogger logger)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger.Write($"Cleanup warning for '{path}': {ex.Message}");
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex StrictVersionRegex();
}

internal sealed class UpdateSettings
{
    public string ManifestUrl { get; set; } = string.Empty;
    public string Channel { get; set; } = "stable";
    public string InstallRoot { get; set; } = string.Empty;
    public string? StatePath { get; set; }
    public string? BackupRoot { get; set; }
    public string ServiceName { get; set; } = "TaskMonitoringEmployeeService";
    public bool RequireSignedPackages { get; set; } = true;
    public string? PublisherCertificateSha256 { get; set; }
    public bool AllowInsecureHttp { get; set; }
    public int BackupRetention { get; set; } = 2;
}

internal sealed class ReleaseManifest
{
    public int SchemaVersion { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime PublishedAtUtc { get; set; }
    public string MinimumUpdaterVersion { get; set; } = "1.0.0";
    public ReleasePackage Package { get; set; } = new();
}

internal sealed class ReleasePackage
{
    public string File { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

internal sealed class InstallState
{
    public string? Version { get; set; }
    public string? Channel { get; set; }
    public DateTime? InstalledAtUtc { get; set; }
    public DateTime? LastSuccessfulUpdateUtc { get; set; }
}

internal sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public FileLogger(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
        Rotate(path);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public void Write(string message)
    {
        lock (_gate)
        {
            var line = $"{DateTime.UtcNow:O} {message}";
            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();

    private static void Rotate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 5 * 1024 * 1024)
        {
            return;
        }

        var backup = path + ".1";
        File.Delete(backup);
        File.Move(path, backup);
    }
}
