using System.Text.Json;
using HandRaise.Application.Zones;

namespace HandRaise.Host.Services;

public sealed class JsonCameraStore(string path) : ICameraStore, IDisposable
{
    private readonly string _path = Expand(path);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task InitializeAsync(IReadOnlyList<CameraDefinition> seed, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (File.Exists(_path)) return;
            foreach (var camera in seed) Validate(camera);
            await WriteAsync(seed, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<CameraDefinition>> ListAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return await ReadAsync(token); }
        finally { _gate.Release(); }
    }

    public async Task<CameraDefinition?> GetAsync(string id, CancellationToken token = default) =>
        (await ListAsync(token)).FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    public async Task AddAsync(CameraDefinition camera, CancellationToken token = default)
    {
        Validate(camera);
        await _gate.WaitAsync(token);
        try
        {
            var values = (await ReadAsync(token)).ToList();
            if (values.Any(x => string.Equals(x.Id, camera.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"La cámara '{camera.Id}' ya existe.");
            values.Add(camera);
            await WriteAsync(values, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> UpdateAsync(CameraDefinition camera, CancellationToken token = default)
    {
        Validate(camera);
        await _gate.WaitAsync(token);
        try
        {
            var values = (await ReadAsync(token)).ToList();
            var index = values.FindIndex(x => string.Equals(x.Id, camera.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            values[index] = camera;
            await WriteAsync(values, token);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var values = (await ReadAsync(token)).ToList();
            var removed = values.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) await WriteAsync(values, token);
            return removed;
        }
        finally { _gate.Release(); }
    }

    public static void Validate(CameraDefinition camera)
    {
        if (string.IsNullOrWhiteSpace(camera.Id) || camera.Id.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("El id de cámara solo admite letras, números, '-' y '_'.");
        if (string.IsNullOrWhiteSpace(camera.Name)) throw new ArgumentException("El nombre de cámara es obligatorio.");
        if (string.IsNullOrWhiteSpace(camera.Source)) throw new ArgumentException("La fuente de cámara es obligatoria.");
        ZoneEditorLogic.Validate(camera.Zones);
    }

    private async Task<CameraDefinition[]> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        var values = await JsonSerializer.DeserializeAsync<CameraDefinition[]>(stream, JsonOptions, token) ?? [];
        foreach (var camera in values) Validate(camera);
        return values;
    }

    private async Task WriteAsync(IEnumerable<CameraDefinition> values, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, values, JsonOptions, token);
        File.Move(temporary, _path, true);
    }

    private static string Expand(string value) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    public void Dispose() => _gate.Dispose();
}
