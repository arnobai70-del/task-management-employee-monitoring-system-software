using System.IO.Compression;
using System.Reflection;

namespace TaskMonitoring.EmployeeSetup;

internal sealed class ExtractedPayload(string directoryPath) : IDisposable
{
    public string DirectoryPath { get; } = directoryPath;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only. Installed files live outside this temporary directory.
        }
    }
}

internal static class SetupPayload
{
    private const string ResourceName = "TaskMonitoring.Payload.zip";
    private static readonly string[] RequiredEntries =
    [
        "release.json",
        "install-employee-windows.ps1",
        "deployment-common.ps1",
        "uninstall-employee-windows.ps1",
        "rollback-employee-windows.ps1",
        "run-central-agent-update.ps1"
    ];

    public static bool HasEmbeddedPayload =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Contains(ResourceName, StringComparer.Ordinal);

    public static ExtractedPayload? ExtractEmbeddedPayload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var names = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in RequiredEntries)
        {
            if (!names.Contains(required))
            {
                throw new InvalidDataException($"Embedded installer payload is missing '{required}'.");
            }
        }

        if (!archive.Entries.Any(entry => entry.FullName.StartsWith("updater/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Embedded installer payload is missing the updater files.");
        }

        var root = Path.Combine(Path.GetTempPath(), "TaskMonitoring.EmployeeSetup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        try
        {
            foreach (var entry in archive.Entries)
            {
                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(root, relative));
                if (!destination.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Embedded installer payload contains an unsafe path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }

            return new ExtractedPayload(root);
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            throw;
        }
    }
}
