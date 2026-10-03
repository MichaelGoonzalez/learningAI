using System.Collections.Concurrent;
using System.Text.Json;
using HandRaise.Application.Rules;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Rules;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class JsonAlertStore : IAlertStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, OperationalAlert> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public JsonAlertStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public async Task<IReadOnlyList<OperationalAlert>> QueryAsync(AlertQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        IEnumerable<OperationalAlert> items = _cache.Values;

        if (!string.IsNullOrWhiteSpace(query.CameraId))
        {
            items = items.Where(a => string.Equals(a.CameraId, query.CameraId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.AnalyticInstanceId))
        {
            items = items.Where(a => string.Equals(a.AnalyticInstanceId, query.AnalyticInstanceId, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Severity.HasValue)
        {
            items = items.Where(a => a.Severity == query.Severity.Value);
        }

        if (query.Status.HasValue)
        {
            items = items.Where(a => a.Status == query.Status.Value);
        }

        if (query.From.HasValue)
        {
            items = items.Where(a => a.CreatedAt >= query.From.Value);
        }

        if (query.To.HasValue)
        {
            items = items.Where(a => a.CreatedAt <= query.To.Value);
        }

        var offset = Math.Max(0, query.Offset);
        var limit = query.Limit > 0 ? query.Limit : 100;

        return items
            .OrderByDescending(a => a.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToArray();
    }

    public async Task<OperationalAlert?> GetByIdAsync(string alertId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alertId)) return null;
        await EnsureLoadedAsync(cancellationToken);
        _cache.TryGetValue(alertId, out var alert);
        return alert;
    }

    public async Task<OperationalAlert> SaveAsync(OperationalAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);
            _cache[alert.Id] = alert;
            await PersistInternalAsync(cancellationToken);
            return alert;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationalAlert?> AcknowledgeAsync(string alertId, DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alertId)) return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);
            if (_cache.TryGetValue(alertId, out var existing))
            {
                var updated = existing with
                {
                    Status = AlertStatus.Acknowledged,
                    AcknowledgedAt = timestampUtc
                };
                _cache[alertId] = updated;
                await PersistInternalAsync(cancellationToken);
                return updated;
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationalAlert?> ResolveAsync(string alertId, DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alertId)) return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);
            if (_cache.TryGetValue(alertId, out var existing))
            {
                var updated = existing with
                {
                    Status = AlertStatus.Resolved,
                    ResolvedAt = timestampUtc
                };
                _cache[alertId] = updated;
                await PersistInternalAsync(cancellationToken);
                return updated;
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ExistsForEventAndRuleAsync(string sourceEventId, string ruleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceEventId) || string.IsNullOrWhiteSpace(ruleId)) return false;
        await EnsureLoadedAsync(cancellationToken);
        return _cache.Values.Any(a =>
            string.Equals(a.SourceEventId, sourceEventId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
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
            var items = await JsonSerializer.DeserializeAsync<List<OperationalAlert>>(
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
