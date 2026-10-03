using System.Collections.Concurrent;
using System.Text.Json;
using HandRaise.Application.Notifications;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Notifications;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class JsonNotificationDestinationStore : INotificationDestinationStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, NotificationDestination> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public JsonNotificationDestinationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public async Task<IReadOnlyList<NotificationDestination>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _cache.Values.ToArray();
    }

    public async Task<NotificationDestination?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        await EnsureLoadedAsync(cancellationToken);
        _cache.TryGetValue(id, out var dest);
        return dest;
    }

    public async Task<NotificationDestination> SaveAsync(NotificationDestination destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Validate();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var normalized = destination with
            {
                CreatedAt = destination.CreatedAt ?? now,
                UpdatedAt = now
            };

            _cache[normalized.Id] = normalized;
            await PersistInternalAsync(cancellationToken);
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);
            if (_cache.TryRemove(id, out _))
            {
                await PersistInternalAsync(cancellationToken);
                return true;
            }
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static NotificationDestination Redact(NotificationDestination dest)
    {
        if (dest.Configuration == null || dest.Configuration.Count == 0) return dest;

        var redactedConfig = new Dictionary<string, object?>(dest.Configuration);

        var secretKeys = new[] { "secret_token", "secret", "password", "token", "api_key", "apikey" };
        foreach (var key in secretKeys)
        {
            if (redactedConfig.ContainsKey(key) && redactedConfig[key] != null)
            {
                redactedConfig[key] = "********";
            }
        }

        if (redactedConfig.TryGetValue("headers", out var headersObj) && headersObj is not null)
        {
            if (headersObj is IReadOnlyDictionary<string, object?> hDict)
            {
                var newHeaders = new Dictionary<string, object?>();
                foreach (var (hKey, hVal) in hDict)
                {
                    if (hKey.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                        hKey.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                        hKey.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                        hKey.Contains("secret", StringComparison.OrdinalIgnoreCase))
                    {
                        newHeaders[hKey] = "********";
                    }
                    else
                    {
                        newHeaders[hKey] = hVal;
                    }
                }
                redactedConfig["headers"] = newHeaders;
            }
            else if (headersObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                var newHeaders = new Dictionary<string, object?>();
                foreach (var prop in je.EnumerateObject())
                {
                    if (prop.Name.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Contains("secret", StringComparison.OrdinalIgnoreCase))
                    {
                        newHeaders[prop.Name] = "********";
                    }
                    else
                    {
                        newHeaders[prop.Name] = prop.Value.ToString();
                    }
                }
                redactedConfig["headers"] = newHeaders;
            }
        }

        return dest with { Configuration = redactedConfig };
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedInternalAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        if (File.Exists(_path))
        {
            await using var stream = File.OpenRead(_path);
            var items = await JsonSerializer.DeserializeAsync<List<NotificationDestination>>(
                stream, AnalyticJsonDefaults.Options, cancellationToken);
            if (items != null)
            {
                foreach (var item in items)
                {
                    _cache[item.Id] = item;
                }
            }
        }
        _initialized = true;
    }

    private async Task PersistInternalAsync(CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var list = _cache.Values.ToList();
        var tempFile = $"{_path}.tmp";
        await using (var stream = File.Create(tempFile))
        {
            await JsonSerializer.SerializeAsync(stream, list, AnalyticJsonDefaults.Options, cancellationToken);
        }

        File.Move(tempFile, _path, true);
    }
}
