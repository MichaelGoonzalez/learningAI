using HandRaise.Domain.Analytics;
using HandRaise.Infrastructure.Windows.Storage;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class JsonAnalyticInstanceStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _filePath;

    public JsonAnalyticInstanceStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"analytic_store_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _filePath = Path.Combine(_tempDirectory, "analytics.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task CrudOperationsWorkCorrectly()
    {
        using var store = new JsonAnalyticInstanceStore(_filePath);

        var initial = await store.GetAllAsync();
        Assert.Empty(initial);

        var instance1 = new CameraAnalyticInstance(
            Id: "an-cam1-handraise",
            CameraId: "cam-1",
            AnalyticTypeId: "hand_raise",
            Name: "Manos Recepción",
            Enabled: true,
            Status: AnalyticStatus.Active,
            Configuration: new Dictionary<string, object?> { ["strict_mode"] = true },
            AssignedZoneIds: ["zone-1"]);

        var saved = await store.SaveAsync(instance1);
        Assert.True(saved);

        var retrieved = await store.GetByIdAsync("an-cam1-handraise");
        Assert.NotNull(retrieved);
        Assert.Equal("Manos Recepción", retrieved.Name);
        Assert.NotNull(retrieved.CreatedAt);
        Assert.NotNull(retrieved.UpdatedAt);

        var byCamera = await store.GetByCameraIdAsync("cam-1");
        Assert.Single(byCamera);
        Assert.Equal("an-cam1-handraise", byCamera[0].Id);

        // Update
        var updatedInstance = retrieved with
        {
            Name = "Manos Recepción Modificado",
            Enabled = false,
            Status = AnalyticStatus.Inactive
        };

        var updated = await store.SaveAsync(updatedInstance);
        Assert.True(updated);

        var afterUpdate = await store.GetByIdAsync("an-cam1-handraise");
        Assert.NotNull(afterUpdate);
        Assert.Equal("Manos Recepción Modificado", afterUpdate.Name);
        Assert.False(afterUpdate.Enabled);
        Assert.Equal(AnalyticStatus.Inactive, afterUpdate.Status);

        // Delete
        var deleted = await store.DeleteAsync("an-cam1-handraise");
        Assert.True(deleted);

        var afterDelete = await store.GetByIdAsync("an-cam1-handraise");
        Assert.Null(afterDelete);
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task InvalidEntitiesThrowArgumentException()
    {
        using var store = new JsonAnalyticInstanceStore(_filePath);

        var invalidId = new CameraAnalyticInstance(
            Id: "invalid ID with spaces",
            CameraId: "cam-1",
            AnalyticTypeId: "hand_raise",
            Name: "Test");

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalidId));
    }
}
