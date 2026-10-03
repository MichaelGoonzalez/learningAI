using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace HandRaise.Host.Services;

public interface INodeCredentialStore
{
    Task<string> GetOrCreateApiKeyAsync(CancellationToken token = default);
    Task<string?> GetApiKeyAsync(CancellationToken token = default);
    Task SaveApiKeyAsync(string apiKey, CancellationToken token = default);
    Task<string> RegenerateApiKeyAsync(CancellationToken token = default);
}

public sealed class JsonNodeCredentialStore(string path, IDataProtector protector) : INodeCredentialStore, IDisposable
{
    private readonly string _path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record StoredNodeCredentials(string ApiKeyProtected, DateTime UpdatedAtUtc);

    public async Task<string?> GetApiKeyAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!File.Exists(_path)) return null;
            var json = await File.ReadAllBytesAsync(_path, token);
            var stored = JsonSerializer.Deserialize<StoredNodeCredentials>(json);
            if (stored is null || string.IsNullOrWhiteSpace(stored.ApiKeyProtected)) return null;

            var decryptedBytes = protector.Unprotect(Convert.FromBase64String(stored.ApiKeyProtected));
            var key = System.Text.Encoding.UTF8.GetString(decryptedBytes);
            return IsValidKey(key) ? key : null;
        }
        catch
        {
            // Fallback ante fallo de desencriptación (ej. traslado a otra máquina con DPAPI máquina)
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> GetOrCreateApiKeyAsync(CancellationToken token = default)
    {
        var existing = await GetApiKeyAsync(token);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var newKey = GenerateSecureKey();
        await SaveApiKeyAsync(newKey, token);
        return newKey;
    }

    public async Task SaveApiKeyAsync(string apiKey, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        await _gate.WaitAsync(token);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var keyBytes = System.Text.Encoding.UTF8.GetBytes(apiKey.Trim());
            var protectedBytes = protector.Protect(keyBytes);
            var stored = new StoredNodeCredentials(Convert.ToBase64String(protectedBytes), DateTime.UtcNow);

            var tempPath = Path.Combine(dir ?? ".", $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, stored, cancellationToken: token);
                    await stream.FlushAsync(token);
                }
                File.Move(tempPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RegenerateApiKeyAsync(CancellationToken token = default)
    {
        var newKey = GenerateSecureKey();
        await SaveApiKeyAsync(newKey, token);
        return newKey;
    }

    public static string GenerateSecureKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var trimmed = key.Trim();
        if (trimmed.Length < 16) return false;
        if (string.Equals(trimmed, "secret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "123456", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "changeme", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    public void Dispose() => _gate.Dispose();
}
