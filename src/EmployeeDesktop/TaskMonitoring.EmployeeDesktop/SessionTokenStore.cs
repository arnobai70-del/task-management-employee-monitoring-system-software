using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskMonitoring.EmployeeDesktop;

public sealed class SessionTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TaskMonitoring.EmployeeDesktop.Session.v1");
    private readonly string _path;

    public SessionTokenStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskMonitoring",
            "EmployeeDesktop");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "session.dat");
    }

    public void Save(string refreshToken, DateTime refreshTokenExpiresAtUtc)
    {
        var json = JsonSerializer.Serialize(new StoredSession(refreshToken, refreshTokenExpiresAtUtc));
        var plaintext = Encoding.UTF8.GetBytes(json);
        var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        var temporaryPath = _path + ".tmp";
        File.WriteAllBytes(temporaryPath, ciphertext);
        File.Move(temporaryPath, _path, true);
    }

    public StoredSession? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var ciphertext = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<StoredSession>(plaintext);
            if (session is null || string.IsNullOrWhiteSpace(session.RefreshToken) || session.RefreshTokenExpiresAtUtc <= DateTime.UtcNow)
            {
                Clear();
                return null;
            }

            return session;
        }
        catch (CryptographicException)
        {
            Clear();
            return null;
        }
        catch (JsonException)
        {
            Clear();
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // A locked token file will be retried on the next application start.
        }
    }

    public sealed record StoredSession(string RefreshToken, DateTime RefreshTokenExpiresAtUtc);
}
