using HandRaise.Application.Zones;
using HandRaise.Infrastructure.Windows.Settings;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class JsonZoneSettingsStoreTests
{
    [Fact]
    public async Task SavesAndLoadsZonesByCamera()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"handraise-zones-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "zones.json");
        try
        {
            using var store = new JsonZoneSettingsStore(path);
            NormalizedZone[] zones = [new("entrada", [new(0.1, 0.1), new(0.9, 0.1), new(0.5, 0.9)])];

            await store.SaveAsync("camera-1", zones);
            var loaded = await store.LoadAsync("camera-1");

            Assert.NotNull(loaded);
            Assert.Equal("entrada", loaded![0].Name);
            Assert.Equal(zones[0].Points, loaded[0].Points);
            Assert.Null(await store.LoadAsync("camera-2"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
