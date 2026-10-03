using HandRaise.Application.Settings;
using HandRaise.Infrastructure.Windows.Settings;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class JsonUserSettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hand-raise-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new JsonUserSettingsStore(path);
            await store.SaveAsync(new UserSettings("directml:adapter"));

            var loaded = await store.LoadAsync();

            Assert.Equal("directml:adapter", loaded.DeviceId);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var store = new JsonUserSettingsStore(path);

        var settings = await store.LoadAsync();

        Assert.Null(settings.DeviceId);
    }
}
