using HandRaise.Application.Settings;

namespace HandRaise.Application.Hardware;

public sealed class DevicePreferenceService(IUserSettingsStore settingsStore)
{
    public async Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await settingsStore.LoadAsync(cancellationToken);

    public async Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default) =>
        await settingsStore.SaveAsync(settings, cancellationToken);

    public async Task<DeviceSelection> InitializeAsync(
        IReadOnlyList<DeviceInfo> devices,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var selection = DeviceSelector.ResolveModeOrDevice(devices, settings.AccelerationMode, settings.DeviceId);

        if (settings.DeviceId is null || selection.UsedFallback)
        {
            await settingsStore.SaveAsync(
                settings with { DeviceId = selection.Device.Id },
                cancellationToken);
        }

        return selection;
    }

    public async Task<DeviceInfo> SelectAsync(
        IReadOnlyList<DeviceInfo> devices,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var selected = devices.FirstOrDefault(
            device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            throw new KeyNotFoundException($"No existe el dispositivo '{deviceId}'.");
        }

        if (!selected.RuntimeAvailable)
        {
            throw new InvalidOperationException(
                selected.UnavailableReason ?? $"El dispositivo '{deviceId}' no está disponible.");
        }

        var current = await settingsStore.LoadAsync(cancellationToken);
        await settingsStore.SaveAsync(current with { DeviceId = selected.Id }, cancellationToken);
        return selected;
    }
}
