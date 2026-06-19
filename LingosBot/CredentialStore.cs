using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LingosBotApp;

internal sealed class CredentialStore
{
    private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("LingosBot.Credentials.v1");

    private readonly AppConfig _config;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public CredentialStore(AppConfig config)
    {
        _config = config;
    }

    public bool TryLoad(out AppCredentials? credentials)
    {
        credentials = null;

        if (!File.Exists(_config.CredentialFilePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_config.CredentialFilePath);
            var stored = JsonSerializer.Deserialize<StoredCredentials>(json, _serializerOptions);

            if (stored is null || string.IsNullOrWhiteSpace(stored.Email) || string.IsNullOrWhiteSpace(stored.ProtectedPassword))
            {
                Console.WriteLine("Saved credentials file is empty or invalid. The app will ask for credentials again.");
                return false;
            }

            var password = UnprotectPassword(stored.ProtectedPassword, stored.ProtectionMode);
            credentials = new AppCredentials(stored.Email.Trim(), password);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or FormatException or NotSupportedException)
        {
            Console.WriteLine($"Could not load saved credentials: {ex.Message}");
            return false;
        }
    }

    public void Save(AppCredentials credentials)
    {
        var protectedPassword = ProtectPassword(credentials.Password, out var protectionMode);
        var payload = new StoredCredentials
        {
            Email = credentials.Email.Trim(),
            ProtectedPassword = protectedPassword,
            ProtectionMode = protectionMode
        };

        var directory = Path.GetDirectoryName(_config.CredentialFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(payload, _serializerOptions);
        File.WriteAllText(_config.CredentialFilePath, json, Encoding.UTF8);
    }

    public void Delete()
    {
        if (File.Exists(_config.CredentialFilePath))
        {
            File.Delete(_config.CredentialFilePath);
        }
    }

    private static string ProtectPassword(string password, out string protectionMode)
    {
        var plainBytes = Encoding.UTF8.GetBytes(password);

        if (OperatingSystem.IsWindows())
        {
            var encryptedBytes = ProtectedData.Protect(plainBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
            protectionMode = "windows-dpapi";
            return Convert.ToBase64String(encryptedBytes);
        }

        protectionMode = "base64";
        return Convert.ToBase64String(plainBytes);
    }

    private static string UnprotectPassword(string protectedPassword, string protectionMode)
    {
        var payloadBytes = Convert.FromBase64String(protectedPassword);

        if (string.Equals(protectionMode, "windows-dpapi", StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows DPAPI credentials can only be decrypted on Windows.");
            }

            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(payloadBytes, AdditionalEntropy, DataProtectionScope.CurrentUser));
        }

        if (protectionMode is "base64" or "")
        {
            return Encoding.UTF8.GetString(payloadBytes);
        }

        throw new NotSupportedException($"Unsupported credential protection mode '{protectionMode}'.");
    }

    private sealed class StoredCredentials
    {
        public string Email { get; set; } = string.Empty;

        public string ProtectedPassword { get; set; } = string.Empty;

        public string ProtectionMode { get; set; } = string.Empty;
    }
}

internal sealed record AppCredentials(string Email, string Password);
