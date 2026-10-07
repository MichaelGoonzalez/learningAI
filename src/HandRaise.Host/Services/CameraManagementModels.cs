using System.Threading.Channels;
using HandRaise.Application.Zones;

namespace HandRaise.Host.Services;

public sealed record CameraDefinition(
    string Id,
    string Name,
    string Source,
    bool Enabled,
    bool Loop,
    IReadOnlyList<NormalizedZone> Zones);

public sealed record CameraWriteRequest(
    string Id,
    string Name,
    string Source,
    bool Enabled = true,
    bool Loop = false,
    string? Username = null,
    string? Password = null);

public sealed record CameraView(
    string Id,
    string Name,
    string Source,
    bool Enabled,
    bool Running,
    bool Online,
    double FramesPerSecond,
    string? Error);

public sealed record CameraTestRequest(string Source, string? Username = null, string? Password = null);
public sealed record CameraTestResult(bool Ok, string? Error, int? Width, int? Height, double? FramesPerSecond, double ConnectionMilliseconds, byte[]? PreviewJpeg = null);
public sealed record DeviceChangeRequest(string DeviceId);
public sealed record DeviceChangeResult(bool Success, string ActiveDeviceId, string? Error, double ElapsedMilliseconds);

public enum CameraStreamStatus
{
    Success,
    NotFound,
    Offline,
    LimitReached
}

public sealed record CameraStreamSubscriptionResult(
    CameraStreamStatus Status,
    ChannelReader<byte[]>? Reader = null,
    IAsyncDisposable? Subscription = null,
    string? Error = null);

public sealed record CameraProbeFrame(
    byte[] Jpeg,
    int Width,
    int Height,
    double FramesPerSecond,
    double LatencyMilliseconds);

public sealed record CameraProbeSubscriptionResult(
    CameraStreamStatus Status,
    ChannelReader<CameraProbeFrame>? Reader = null,
    IAsyncDisposable? Subscription = null,
    string? Error = null);

public interface ICameraManagementService
{
    Task<IReadOnlyList<CameraView>> ListAsync(CancellationToken token = default);
    Task<CameraView?> GetAsync(string id, CancellationToken token = default);
    Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default);
    Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default);
    Task<bool> DeleteAsync(string id, CancellationToken token = default);
    Task<CameraView?> StartAsync(string id, CancellationToken token = default);
    Task<CameraView?> StopAsync(string id, CancellationToken token = default);
    Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default);
    Task<CameraProbeSubscriptionResult> ProbeStreamAsync(CameraTestRequest request, double targetFps = 15.0, int quality = 70, CancellationToken token = default) =>
        Task.FromResult(new CameraProbeSubscriptionResult(CameraStreamStatus.Offline, Error: "No implementado."));
    Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default);
    Task<IReadOnlyList<NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default);
    Task<bool> UpdateZonesAsync(string id, IReadOnlyList<NormalizedZone> zones, CancellationToken token = default);
    Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default);
    Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default);
}

public interface ICameraStore
{
    Task InitializeAsync(IReadOnlyList<CameraDefinition> seed, CancellationToken token = default);
    Task<IReadOnlyList<CameraDefinition>> ListAsync(CancellationToken token = default);
    Task<CameraDefinition?> GetAsync(string id, CancellationToken token = default);
    Task AddAsync(CameraDefinition camera, CancellationToken token = default);
    Task<bool> UpdateAsync(CameraDefinition camera, CancellationToken token = default);
    Task<bool> DeleteAsync(string id, CancellationToken token = default);
}

public sealed record CameraCredentials(string Username, string Password);

public interface ICameraCredentialStore
{
    Task<CameraCredentials?> GetAsync(string cameraId, CancellationToken token = default);
    Task SaveAsync(string cameraId, CameraCredentials credentials, CancellationToken token = default);
    Task DeleteAsync(string cameraId, CancellationToken token = default);
}

internal sealed class DisabledCameraManagementService : ICameraManagementService
{
    public Task<IReadOnlyList<CameraView>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CameraView>>([]);
    public Task<CameraView?> GetAsync(string id, CancellationToken token = default) => Task.FromResult<CameraView?>(null);
    public Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default) => throw Disabled();
    public Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default) => throw Disabled();
    public Task<bool> DeleteAsync(string id, CancellationToken token = default) => throw Disabled();
    public Task<CameraView?> StartAsync(string id, CancellationToken token = default) => throw Disabled();
    public Task<CameraView?> StopAsync(string id, CancellationToken token = default) => throw Disabled();
    public Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default) => throw Disabled();
    public Task<CameraProbeSubscriptionResult> ProbeStreamAsync(CameraTestRequest request, double targetFps = 15.0, int quality = 70, CancellationToken token = default) =>
        Task.FromResult(new CameraProbeSubscriptionResult(CameraStreamStatus.Offline, Error: "La gestión de cámaras está deshabilitada en este host."));
    public Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
    public Task<IReadOnlyList<NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default) => Task.FromResult<IReadOnlyList<NormalizedZone>?>(null);
    public Task<bool> UpdateZonesAsync(string id, IReadOnlyList<NormalizedZone> zones, CancellationToken token = default) => throw Disabled();
    public Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default) => throw Disabled();
    public Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default) =>
        Task.FromResult(new CameraStreamSubscriptionResult(CameraStreamStatus.Offline, Error: "La gestión de cámaras está deshabilitada en este host."));
    private static InvalidOperationException Disabled() => new("La gestión de cámaras está deshabilitada en este host.");
}
