using System.Collections.Concurrent;
using System.Text.Json;
using HandRaise.Application.Lines;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Lines;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class JsonLineStore : ILineStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, List<LineDefinition>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public JsonLineStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public IReadOnlyList<LineDefinition> GetLines(string cameraId)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return [];
        EnsureLoaded();
        return _cache.TryGetValue(cameraId, out var lines) ? lines.ToArray() : [];
    }

    public async Task<IReadOnlyList<LineDefinition>> ListByCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return [];
        await EnsureLoadedAsync(cancellationToken);
        return _cache.TryGetValue(cameraId, out var lines) ? lines.ToArray() : [];
    }

    public async Task<LineDefinition?> GetByIdAsync(string cameraId, string lineId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || string.IsNullOrWhiteSpace(lineId)) return null;
        await EnsureLoadedAsync(cancellationToken);
        if (_cache.TryGetValue(cameraId, out var lines))
        {
            return lines.FirstOrDefault(l => string.Equals(l.Id, lineId, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    public async Task<LineDefinition> SaveAsync(string cameraId, LineDefinition line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(cameraId)) throw new ArgumentException("CameraId no puede estar vacío.", nameof(cameraId));
        line.Validate();

        var normalizedLine = line with { CameraId = cameraId };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);

            var list = _cache.GetOrAdd(cameraId, _ => new List<LineDefinition>());
            var existingIndex = list.FindIndex(l => string.Equals(l.Id, normalizedLine.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                list[existingIndex] = normalizedLine;
            }
            else
            {
                list.Add(normalizedLine);
            }

            await PersistInternalAsync(cancellationToken);
            return normalizedLine;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LineDefinition>> SaveAllAsync(string cameraId, IReadOnlyList<LineDefinition> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (string.IsNullOrWhiteSpace(cameraId)) throw new ArgumentException("CameraId no puede estar vacío.", nameof(cameraId));

        var validated = new List<LineDefinition>(lines.Count);
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            line.Validate();
            validated.Add(line with { CameraId = cameraId });
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);
            _cache[cameraId] = validated;
            await PersistInternalAsync(cancellationToken);
            return validated.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string cameraId, string lineId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || string.IsNullOrWhiteSpace(lineId)) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedInternalAsync(cancellationToken);
            if (_cache.TryGetValue(cameraId, out var list))
            {
                var count = list.RemoveAll(l => string.Equals(l.Id, lineId, StringComparison.OrdinalIgnoreCase));
                if (count > 0)
                {
                    await PersistInternalAsync(cancellationToken);
                    return true;
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
            if (_cache.TryRemove(cameraId, out var removed))
            {
                await PersistInternalAsync(cancellationToken);
                return removed.Count;
            }
            return 0;
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
                var doc = JsonSerializer.Deserialize<Dictionary<string, List<LineDefinition>>>(json, AnalyticJsonDefaults.Options);
                if (doc != null)
                {
                    foreach (var (k, v) in doc)
                    {
                        _cache[k] = v;
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
            var doc = await JsonSerializer.DeserializeAsync<Dictionary<string, List<LineDefinition>>>(
                stream, AnalyticJsonDefaults.Options, cancellationToken);
            if (doc != null)
            {
                foreach (var (k, v) in doc)
                {
                    _cache[k] = v;
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

        var dict = new Dictionary<string, List<LineDefinition>>(_cache, StringComparer.OrdinalIgnoreCase);
        var tempFile = $"{_path}.tmp";
        await using (var stream = File.Create(tempFile))
        {
            await JsonSerializer.SerializeAsync(stream, dict, AnalyticJsonDefaults.Options, cancellationToken);
        }

        File.Move(tempFile, _path, true);
    }
}

