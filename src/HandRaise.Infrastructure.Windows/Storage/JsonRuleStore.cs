using System.Collections.Concurrent;
using System.Text.Json;
using HandRaise.Application.Rules;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Rules;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class JsonRuleStore : IRuleStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, AlertRule> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public JsonRuleStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public IReadOnlyList<AlertRule> GetRules(string cameraId)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return [];
        EnsureLoaded();
        return _cache.Values
            .Where(r => string.Equals(r.CameraId, cameraId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public IReadOnlyList<AlertRule> GetAllRules()
    {
        EnsureLoaded();
        return _cache.Values.ToArray();
    }

    public async Task<IReadOnlyList<AlertRule>> ListByInstanceAsync(string cameraId, string instanceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || string.IsNullOrWhiteSpace(instanceId)) return [];
        await EnsureLoadedAsync(cancellationToken);
        return _cache.Values
            .Where(r => string.Equals(r.CameraId, cameraId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.AnalyticInstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task<IReadOnlyList<AlertRule>> ListByCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return [];
        await EnsureLoadedAsync(cancellationToken);
        return _cache.Values
            .Where(r => string.Equals(r.CameraId, cameraId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task<IReadOnlyList<AlertRule>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _cache.Values.ToArray();
    }

    public async Task<AlertRule?> GetByIdAsync(string cameraId, string instanceId, string ruleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) return null;
        await EnsureLoadedAsync(cancellationToken);
        if (_cache.TryGetValue(ruleId, out var rule))
        {
            if (string.Equals(rule.CameraId, cameraId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rule.AnalyticInstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }
        return null;
    }

    public async Task<AlertRule> SaveAsync(AlertRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rule.Validate();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var normalizedRule = rule with
            {
                CreatedAt = rule.CreatedAt ?? now,
                UpdatedAt = now
            };

            _cache[normalizedRule.Id] = normalizedRule;
            await PersistInternalAsync(cancellationToken);
            return normalizedRule;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string cameraId, string instanceId, string ruleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);
            if (_cache.TryGetValue(ruleId, out var rule))
            {
                if (string.Equals(rule.CameraId, cameraId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(rule.AnalyticInstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                {
                    if (_cache.TryRemove(ruleId, out _))
                    {
                        await PersistInternalAsync(cancellationToken);
                        return true;
                    }
                }
            }
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> DeleteByCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return 0;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);
            var toRemove = _cache.Values
                .Where(r => string.Equals(r.CameraId, cameraId, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Id)
                .ToList();

            var count = 0;
            foreach (var id in toRemove)
            {
                if (_cache.TryRemove(id, out _)) count++;
            }

            if (count > 0)
            {
                await PersistInternalAsync(cancellationToken);
            }
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_initialized) return;
        _gate.Wait();
        try
        {
            if (_initialized) return;
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var items = JsonSerializer.Deserialize<List<AlertRule>>(json, AnalyticJsonDefaults.Options);
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
            var items = await JsonSerializer.DeserializeAsync<List<AlertRule>>(
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
