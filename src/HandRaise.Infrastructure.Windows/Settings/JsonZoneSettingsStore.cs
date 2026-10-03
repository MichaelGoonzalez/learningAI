using System.Text.Json;
using HandRaise.Application.Zones;

namespace HandRaise.Infrastructure.Windows.Settings;

public sealed class JsonZoneSettingsStore : IZoneSettingsStore, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonZoneSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandRaiseDetection", "zones.json");
    }

    public async Task<IReadOnlyList<NormalizedZone>?> LoadAsync(
        string cameraId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(cancellationToken);
            return document.TryGetValue(cameraId, out var zones) ? zones : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string cameraId,
        IReadOnlyList<NormalizedZone> zones,
        CancellationToken cancellationToken = default)
    {
        ZoneEditorLogic.Validate(zones);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(cancellationToken);
            document[cameraId] = zones.ToArray();
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(
                temporary, JsonSerializer.Serialize(document, _json), cancellationToken);
            File.Move(temporary, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<Dictionary<string, NormalizedZone[]>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(_path))
        {
            return new(StringComparer.Ordinal);
        }
        var json = await File.ReadAllTextAsync(_path, token);
        return JsonSerializer.Deserialize<Dictionary<string, NormalizedZone[]>>(json, _json)
            ?? new(StringComparer.Ordinal);
    }
}
