using System.Text.Json;
using HandRaise.Application.Analytics;
using HandRaise.Domain.Analytics;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class JsonAnalyticInstanceStore : IAnalyticInstanceStore, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(AnalyticJsonDefaults.Options) { WriteIndented = true };

    public JsonAnalyticInstanceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public async Task<IReadOnlyList<CameraAnalyticInstance>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CameraAnalyticInstance>> GetByCameraIdAsync(
        string cameraId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return [];
        var all = await GetAllAsync(cancellationToken);
        return all.Where(x => string.Equals(x.CameraId, cameraId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public async Task<CameraAnalyticInstance?> GetByIdAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        var all = await GetAllAsync(cancellationToken);
        return all.FirstOrDefault(x => string.Equals(x.Id, instanceId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> SaveAsync(
        CameraAnalyticInstance instance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Validate(instance);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await ReadAsync(cancellationToken)).ToList();
            var index = items.FindIndex(x => string.Equals(x.Id, instance.Id, StringComparison.OrdinalIgnoreCase));

            var now = DateTimeOffset.UtcNow;
            var updatedInstance = instance with
            {
                CreatedAt = instance.CreatedAt ?? (index >= 0 ? items[index].CreatedAt ?? now : now),
                UpdatedAt = now
            };

            if (index >= 0)
            {
                items[index] = updatedInstance;
            }
            else
            {
                items.Add(updatedInstance);
            }

            await WriteAsync(items, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await ReadAsync(cancellationToken)).ToList();
            var removed = items.RemoveAll(x => string.Equals(x.Id, instanceId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                await WriteAsync(items, cancellationToken);
            }
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static void Validate(CameraAnalyticInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.Id) || instance.Id.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("El id de la instancia analítica solo admite letras, números, '-' y '_'.");
        if (string.IsNullOrWhiteSpace(instance.CameraId))
            throw new ArgumentException("El id de cámara es obligatorio.");
        if (string.IsNullOrWhiteSpace(instance.AnalyticTypeId))
            throw new ArgumentException("El tipo de analítica es obligatorio.");
        if (string.IsNullOrWhiteSpace(instance.Name))
            throw new ArgumentException("El nombre de la instancia analítica es obligatorio.");
    }

    private async Task<CameraAnalyticInstance[]> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        var values = await JsonSerializer.DeserializeAsync<CameraAnalyticInstance[]>(stream, JsonOptions, token) ?? [];
        foreach (var item in values) Validate(item);
        return values;
    }

    private async Task WriteAsync(IEnumerable<CameraAnalyticInstance> values, CancellationToken token)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(dir ?? ".", $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, values, JsonOptions, cancellationToken: token);
                await stream.FlushAsync(token);
            }
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public void Dispose() => _gate.Dispose();
}
