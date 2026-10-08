using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HandRaise.Application.Events;
using HandRaise.Application.Storage;
using HandRaise.Host;
using HandRaise.Host.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandRaise.Host.Tests;

public sealed class RealtimeApiTests
{
    [Fact]
    public async Task WsEventsRejectsUnauthorizedAndAcceptsConfiguredKey()
    {
        await using var app = CreateApp("secret", new StreamingTestCameraManager());
        await app.StartAsync();
        var server = app.GetTestServer();

        // 1. Without auth -> should fail connection (handshake 401)
        var unauthorizedWsClient = server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await unauthorizedWsClient.ConnectAsync(new Uri(server.BaseAddress, "/api/v1/ws/events"), cts.Token);
        });

        // 2. With header auth -> succeeds
        var headerWsClient = server.CreateWebSocketClient();
        headerWsClient.ConfigureRequest = req => req.Headers["X-Api-Key"] = "secret";
        using var headerWs = await headerWsClient.ConnectAsync(new Uri(server.BaseAddress, "/api/v1/ws/events"), default);
        Assert.Equal(WebSocketState.Open, headerWs.State);
        await headerWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", default);

        // 3. With query param auth -> succeeds
        var queryWsClient = server.CreateWebSocketClient();
        queryWsClient.ConfigureRequest = req => req.QueryString = new Microsoft.AspNetCore.Http.QueryString("?api_key=secret");
        using var queryWs = await queryWsClient.ConnectAsync(new Uri(server.BaseAddress, "/api/v1/ws/events"), default);
        Assert.Equal(WebSocketState.Open, queryWs.State);
        await queryWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", default);
    }

    [Fact]
    public async Task WsEventsFiltersByCameraId()
    {
        await using var app = CreateApp("secret", new StreamingTestCameraManager());
        await app.StartAsync();
        var server = app.GetTestServer();
        var eventBus = app.Services.GetRequiredService<HandEventBus>();

        var wsClient = server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
        {
            req.Headers["X-Api-Key"] = "secret";
            req.QueryString = new Microsoft.AspNetCore.Http.QueryString("?camera_id=cam-target");
        };
        using var ws = await wsClient.ConnectAsync(new Uri(server.BaseAddress, "/api/v1/ws/events"), default);
        await Task.Delay(100);

        // Publish event for target camera and another camera
        await eventBus.PublishAsync(Event("ev-other", "cam-other", "hand_raised", 1));
        await eventBus.PublishAsync(Event("ev-target", "cam-target", "hand_raised", 2));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedJson = await ReceiveTextAsync(ws, cts.Token);
        using var doc = JsonDocument.Parse(receivedJson);

        Assert.Equal("ev-target", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("cam-target", doc.RootElement.GetProperty("camera_id").GetString());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", default);
    }

    [Fact]
    public async Task WsEventsChannelDoesNotBlockPipelineWhenClientIsSlow()
    {
        await using var app = CreateApp("secret", new StreamingTestCameraManager(), eventChannelCapacity: 16);
        await app.StartAsync();
        var server = app.GetTestServer();
        var eventBus = app.Services.GetRequiredService<HandEventBus>();

        var wsClient = server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req => req.Headers["X-Api-Key"] = "secret";
        using var ws = await wsClient.ConnectAsync(new Uri(server.BaseAddress, "/api/v1/ws/events"), default);
        await Task.Delay(100);

        // Rapidly publish 40 events to a channel with capacity 16 without reading
        for (var i = 0; i < 40; i++)
        {
            await eventBus.PublishAsync(Event($"ev-{i}", "cam-1", "hand_raised", i));
        }

        // Reading now should still succeed without blocking
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var msg = await ReceiveTextAsync(ws, cts.Token);
        Assert.NotNull(msg);
        Assert.NotEmpty(msg);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", default);
    }

    [Fact]
    public async Task WsMetricsSendsSnapshotsPeriodically()
    {
        await using var app = CreateApp("secret", new StreamingTestCameraManager());
        await app.StartAsync();
        var server = app.GetTestServer();

        var wsClient = server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
        {
            req.Headers["X-Api-Key"] = "secret";
            req.QueryString = new Microsoft.AspNetCore.Http.QueryString("?interval_seconds=0.1");
        };
        using var ws = await wsClient.ConnectAsync(new Uri(server.BaseAddress, "/api/v1/ws/metrics"), default);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var msg1 = await ReceiveTextAsync(ws, cts.Token);
        using var doc1 = JsonDocument.Parse(msg1);
        Assert.Equal("node-test", doc1.RootElement.GetProperty("node_id").GetString());
        Assert.Equal("site-test", doc1.RootElement.GetProperty("site_id").GetString());
        Assert.True(doc1.RootElement.TryGetProperty("pipeline", out _));

        var msg2 = await ReceiveTextAsync(ws, cts.Token);
        using var doc2 = JsonDocument.Parse(msg2);
        Assert.Equal("node-test", doc2.RootElement.GetProperty("node_id").GetString());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", default);
    }

    [Fact]
    public async Task MjpegStreamReturnsMultipartHeadersAndJpegFrames()
    {
        var cameraManager = new StreamingTestCameraManager();
        await using var app = CreateApp("secret", cameraManager);
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var response = await client.GetAsync("/api/v1/cameras/cam-1/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("multipart/x-mixed-replace", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[1024];
        var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
        var text = Encoding.ASCII.GetString(buffer, 0, read);

        Assert.Contains("--frame", text, StringComparison.Ordinal);
        Assert.Contains("image/jpeg", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MjpegStreamEnforcesMaxClientsLimitAndReleasesOnDisconnect()
    {
        var cameraManager = new StreamingTestCameraManager(maxClients: 2);
        await using var app = CreateApp("secret", cameraManager);
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        var stream1 = await client.GetAsync("/api/v1/cameras/cam-1/stream", HttpCompletionOption.ResponseHeadersRead, cts1.Token);
        var stream2 = await client.GetAsync("/api/v1/cameras/cam-1/stream", HttpCompletionOption.ResponseHeadersRead, cts2.Token);

        Assert.Equal(HttpStatusCode.OK, stream1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stream2.StatusCode);

        // 3rd client should hit 429 Too Many Requests
        var stream3 = await client.GetAsync("/api/v1/cameras/cam-1/stream");
        Assert.Equal(HttpStatusCode.TooManyRequests, stream3.StatusCode);

        // Disconnect client 1
        cts1.Cancel();
        stream1.Dispose();

        for (var i = 0; i < 20 && cameraManager.ActiveClients > 1; i++)
        {
            await Task.Delay(50);
        }

        // Now a new client should be able to connect
        using var cts4 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stream4 = await client.GetAsync("/api/v1/cameras/cam-1/stream", HttpCompletionOption.ResponseHeadersRead, cts4.Token);
        Assert.Equal(HttpStatusCode.OK, stream4.StatusCode);

        cts2.Cancel();
        stream2.Dispose();
        cts4.Cancel();
        stream4.Dispose();
    }

    [Fact]
    public async Task MjpegStreamReturns404ForMissingCamera()
    {
        await using var app = CreateApp("secret", new StreamingTestCameraManager());
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/cameras/missing-cam/stream");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken token)
    {
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static WebApplication CreateApp(
        string? apiKey,
        ICameraManagementService cameraManager,
        int eventChannelCapacity = 128) =>
        HostApplication.Build([], startCameras: false, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Environment.EnvironmentName = "Production";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["api:apiKey"] = apiKey,
                ["api:apiKeyHeader"] = "X-Api-Key",
                ["nodeId"] = "node-test",
                ["siteId"] = "site-test",
                ["capacity:targetFramesPerSecond"] = "5",
                ["storage:snapshotDirectory"] = Path.GetTempPath(),
                ["streaming:eventChannelCapacity"] = eventChannelCapacity.ToString()
            });
            builder.Services.AddSingleton<IHandEventRepository>(new ReadApiTests.FakeRepositoryForD2());
            builder.Services.AddSingleton<ICameraManagementService>(cameraManager);
        });

    private static HandEvent Event(string id, string camera, string type, int minute) => new(
        id, type, camera, 1, "left", "zone", 0.9,
        new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero),
        NodeId: "node-test",
        SiteId: "site-test");

    private sealed class StreamingTestCameraManager(int maxClients = 4) : ICameraManagementService
    {
        public event Action<string, bool>? CameraRunningStateChanged;
        public event Action? CamerasChanged;

        private int _activeClients;
        public int ActiveClients => Volatile.Read(ref _activeClients);
        private readonly byte[] _dummyJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

        public Task<IReadOnlyList<CameraView>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CameraView>>([]);
        public Task<CameraView?> GetAsync(string id, CancellationToken token = default) => Task.FromResult<CameraView?>(null);
        public Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CameraView?> StartAsync(string id, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CameraView?> StopAsync(string id, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default) => throw new NotImplementedException();
        public Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<HandRaise.Application.Zones.NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default) => Task.FromResult<IReadOnlyList<HandRaise.Application.Zones.NormalizedZone>?>(null);
        public Task<bool> UpdateZonesAsync(string id, IReadOnlyList<HandRaise.Application.Zones.NormalizedZone> zones, CancellationToken token = default) => throw new NotImplementedException();
        public Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default) => throw new NotImplementedException();

        public Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default)
        {
            if (id == "missing-cam")
                return Task.FromResult(new CameraStreamSubscriptionResult(CameraStreamStatus.NotFound));

            if (Interlocked.Increment(ref _activeClients) > maxClients)
            {
                Interlocked.Decrement(ref _activeClients);
                return Task.FromResult(new CameraStreamSubscriptionResult(CameraStreamStatus.LimitReached, Error: "Límite alcanzado"));
            }

            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

            // Seed a frame
            channel.Writer.TryWrite(_dummyJpeg);

            var subscription = new TestStreamSubscription(() =>
            {
                Interlocked.Decrement(ref _activeClients);
                channel.Writer.TryComplete();
            });

            return Task.FromResult(new CameraStreamSubscriptionResult(
                CameraStreamStatus.Success, channel.Reader, subscription));
        }

        private sealed class TestStreamSubscription(Action onDispose) : IAsyncDisposable
        {
            private bool _disposed;
            public ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    onDispose();
                }
                return ValueTask.CompletedTask;
            }
        }
    }
}
