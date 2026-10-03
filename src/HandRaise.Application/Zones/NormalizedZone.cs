using HandRaise.Domain.Zones;

namespace HandRaise.Application.Zones;

public sealed record NormalizedPoint(double X, double Y);

public sealed record NormalizedZone(string Name, IReadOnlyList<NormalizedPoint> Points);

public interface IZoneProvider
{
    IReadOnlyList<ZoneDefinition> GetZones(int frameWidth, int frameHeight);
}

public interface IZoneSettingsStore
{
    Task<IReadOnlyList<NormalizedZone>?> LoadAsync(
        string cameraId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string cameraId,
        IReadOnlyList<NormalizedZone> zones,
        CancellationToken cancellationToken = default);
}
