using System.Collections.Concurrent;
using System.Text.Json;
using HandRaise.Application.Notifications;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Notifications;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class JsonNotificationPolicyStore : INotificationPolicyStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, NotificationPolicy> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public JsonNotificationPolicyStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public async Task<IReadOnlyList<NotificationPolicy>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _cache.Values.ToArray();
    }

    public async Task<NotificationPolicy?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        await EnsureLoadedAsync(cancellationToken);
        _cache.TryGetValue(id, out var policy);
        return policy;
    }

    public async Task<NotificationPolicy> SaveAsync(NotificationPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var normalized = policy with
            {
                CreatedAt = policy.CreatedAt ?? now,
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
            var items = await JsonSerializer.DeserializeAsync<List<NotificationPolicy>>(
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
