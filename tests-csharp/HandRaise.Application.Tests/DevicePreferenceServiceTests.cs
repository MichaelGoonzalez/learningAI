using HandRaise.Application.Hardware;
using HandRaise.Application.Settings;

namespace HandRaise.Application.Tests;

public sealed class DevicePreferenceServiceTests
{
    [Fact]
    public async Task InitializeAsync_WhenSavedDeviceIsMissing_PersistsCpuFallback()
    {
        var store = new MemorySettingsStore(new UserSettings("cuda:missing"));
        var cpu = new DeviceInfo(
            "cpu", "CPU", HardwareVendor.Cpu, InferenceBackend.Cpu, null, null, null, true);
        var service = new DevicePreferenceService(store);

        var selection = await service.InitializeAsync([cpu]);

        Assert.True(selection.UsedFallback);
        Assert.Equal("cpu", store.Settings.DeviceId);
    }

    [Fact]
    public async Task SelectAsync_RejectsUnavailableRuntimeWithoutChangingSettings()
    {
        var store = new MemorySettingsStore(new UserSettings("cpu"));
        var gpu = new DeviceInfo(
            "dml", "GPU", HardwareVendor.Amd, InferenceBackend.DirectMl,
            0, 4096, null, false, "DirectML ausente");
        var service = new DevicePreferenceService(store);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SelectAsync([gpu], gpu.Id));

        Assert.Contains("DirectML", error.Message);
        Assert.Equal("cpu", store.Settings.DeviceId);
    }

    [Fact]
    public void ResolveModeOrDevice_ResolvesCpuWhenCpuModeExplicitlyRequested()
    {
        var cpu = new DeviceInfo("cpu", "CPU", HardwareVendor.Cpu, InferenceBackend.Cpu, null, null, null, true);
        var gpu = new DeviceInfo("dml", "DirectML GPU", HardwareVendor.Nvidia, InferenceBackend.DirectMl, 0, 8192, null, true);

        var selection = DeviceSelector.ResolveModeOrDevice([cpu, gpu], "cpu", "dml");

        Assert.Equal("cpu", selection.Device.Id);
        Assert.False(selection.UsedFallback);
    }

    [Fact]
    public void ResolveModeOrDevice_ResolvesDirectMlWhenGpuModeRequested()
    {
        var cpu = new DeviceInfo("cpu", "CPU", HardwareVendor.Cpu, InferenceBackend.Cpu, null, null, null, true);
        var gpu = new DeviceInfo("dml", "DirectML GPU", HardwareVendor.Nvidia, InferenceBackend.DirectMl, 0, 8192, null, true);

        var selection = DeviceSelector.ResolveModeOrDevice([cpu, gpu], "gpu", null);

        Assert.Equal("dml", selection.Device.Id);
        Assert.False(selection.UsedFallback);
    }

    [Fact]
    public void ResolveModeOrDevice_FallsBackToCpuWhenGpuRequestedButUnavailable()
    {
        var cpu = new DeviceInfo("cpu", "CPU", HardwareVendor.Cpu, InferenceBackend.Cpu, null, null, null, true);
        var gpu = new DeviceInfo("dml", "DirectML GPU", HardwareVendor.Nvidia, InferenceBackend.DirectMl, 0, 8192, null, false, "Driver incompatible");

        var selection = DeviceSelector.ResolveModeOrDevice([cpu, gpu], "gpu", null);

        Assert.Equal("cpu", selection.Device.Id);
        Assert.True(selection.UsedFallback);
        Assert.NotNull(selection.Warning);
    }

    private sealed class MemorySettingsStore(UserSettings initial) : IUserSettingsStore
    {
        public string FilePath => "memory://settings";

        public UserSettings Settings { get; private set; } = initial;

        public Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings);

        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }
}
