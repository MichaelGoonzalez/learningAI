using System.Collections.Concurrent;
using System.Text.Json;
using HandRaise.Application.Notifications;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Notifications;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class JsonNotificationAttemptStore : INotificationAttemptStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, NotificationAttempt> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public JsonNotificationAttemptStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public async Task<NotificationAttempt> SaveAsync(NotificationAttempt attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);

            _cache[attempt.Id] = attempt;
            await PersistInternalAsync(cancellationToken);
            return attempt;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<NotificationAttempt>> QueryAsync(NotificationAttemptQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        IEnumerable<NotificationAttempt> items = _cache.Values;

        if (!string.IsNullOrWhiteSpace(query.AlertId))
        {
            items = items.Where(a => string.Equals(a.AlertId, query.AlertId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.DestinationId))
        {
            items = items.Where(a => string.Equals(a.DestinationId, query.DestinationId, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Status.HasValue)
        {
            items = items.Where(a => a.Status == query.Status.Value);
        }

        var offset = Math.Max(0, query.Offset);
        var limit = query.Limit > 0 ? query.Limit : 100;

        return items
            .OrderByDescending(a => a.TimestampUtc)
            .Skip(offset)
            .Take(limit)
            .ToArray();
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
            var items = await JsonSerializer.DeserializeAsync<List<NotificationAttempt>>(
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
