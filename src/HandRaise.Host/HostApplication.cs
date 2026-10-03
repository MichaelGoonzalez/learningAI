using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HandRaise.Application.Events;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Storage;
using HandRaise.Host.Api;
using HandRaise.Host.Configuration;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Storage;
using Microsoft.EntityFrameworkCore;

namespace HandRaise.Host;

public static class HostApplication
{
    public static WebApplication Build(
        string[] args,
        bool startCameras = true,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(options => options.ServiceName = "HandRaise Detection");
        configure?.Invoke(builder);
        var options = builder.Configuration.Get<Configuration.HostOptions>() ?? new Configuration.HostOptions();
        if (string.IsNullOrWhiteSpace(options.NodeId) || string.IsNullOrWhiteSpace(options.SiteId))
            throw new InvalidOperationException("nodeId y siteId son obligatorios.");
        if (!double.IsFinite(options.Capacity.TargetFramesPerSecond) || options.Capacity.TargetFramesPerSecond <= 0)
            throw new InvalidOperationException("capacity.targetFramesPerSecond debe ser mayor que cero.");
        builder.WebHost.UseUrls($"http://{options.Api.BindAddress}:{options.Api.Port}");

        NodeBootstrapper.Bootstrap(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(options.Api);
        builder.Services.AddSingleton(new PipelineMetricsRegistry(options.NodeId, options.SiteId));
        builder.Services.AddSingleton<HostRuntimeState>();
        builder.Services.AddSingleton<HandEventBus>();
        builder.Services.AddSingleton<PreflightChecker>();
        builder.Services.AddSingleton<DiagnosticsProvider>();
        if (!builder.Services.Any(item => item.ServiceType == typeof(ICameraStore)))
            builder.Services.AddSingleton<ICameraStore>(_ => new JsonCameraStore(options.Storage.CameraStorePath));
        if (!builder.Services.Any(item => item.ServiceType == typeof(ICameraCredentialStore)))
            builder.Services.AddSingleton<ICameraCredentialStore>(_ => new JsonCameraCredentialStore(
                options.Storage.CameraCredentialStorePath, new MachineDpapiProtector()));
        if (!builder.Services.Any(item => item.ServiceType == typeof(IHandEventRepository)))
        {
            builder.Services.AddSingleton<IHandEventRepository>(_ =>
                new SqliteHandEventRepository(StorageDatabase.CreateOptions(options.Storage.DatabasePath)));
        }
        builder.Services.AddSingleton(provider => new EventReadService(
            provider.GetRequiredService<IHandEventRepository>(), options.Storage.SnapshotDirectory));
        if (startCameras)
        {
            builder.Services.AddSingleton<HeadlessEngineService>();
            builder.Services.AddSingleton<ICameraManagementService>(provider => provider.GetRequiredService<HeadlessEngineService>());
            builder.Services.AddHostedService(provider => provider.GetRequiredService<HeadlessEngineService>());
        }
        else if (!builder.Services.Any(item => item.ServiceType == typeof(ICameraManagementService)))
        {
            builder.Services.AddSingleton<ICameraManagementService, DisabledCameraManagementService>();
        }

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddCors(cors => cors.AddPolicy("Configured", policy =>
        {
            if (options.Api.AllowedOrigins.Length > 0)
                policy.WithOrigins(options.Api.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
        }));

        var app = builder.Build();
        var metrics = app.Services.GetRequiredService<PipelineMetricsRegistry>();
        foreach (var camera in options.Cameras) metrics.Register(camera.Id, camera.Name);
        app.UseExceptionHandler();
        app.UseCors("Configured");
        app.UseWebSockets();
        app.UseMiddleware<ApiKeyMiddleware>();
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        MapApi(app);
        return app;
    }

    private static void MapApi(WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/health", (PipelineMetricsRegistry metrics, Configuration.HostOptions options) => Results.Ok(new
        {
            status = "healthy",
            node_id = options.NodeId,
            site_id = options.SiteId,
            uptime_seconds = metrics.Snapshot().UptimeSeconds
        }));
        api.MapGet("/health/readiness", (PreflightChecker checker, PipelineMetricsRegistry metrics, Configuration.HostOptions options) =>
            Results.Ok(checker.BuildReport(metrics.Snapshot().UptimeSeconds, options.NodeId, options.SiteId, isRunning: true)));
        api.MapGet("/system/diagnostics", (DiagnosticsProvider diagnostics) =>
            Results.Ok(diagnostics.GetDiagnostics()));
        api.MapGet("/metrics", (PipelineMetricsRegistry metrics, HostRuntimeState runtime, Configuration.HostOptions options) => Results.Ok(new
        {
            node_id = options.NodeId,
            site_id = options.SiteId,
            pipeline = metrics.Snapshot(),
            active_device_id = runtime.ActiveDeviceId,
            model = runtime.ModelName
        }));
        api.MapGet("/devices", (HostRuntimeState runtime) => Results.Ok(new
        {
            active_device_id = runtime.ActiveDeviceId,
            items = runtime.Inventory.Devices,
            warnings = runtime.Inventory.Warnings
        }));
        api.MapGet("/cameras", async (ICameraManagementService service, CancellationToken token) =>
            Results.Ok(await service.ListAsync(token)));
        api.MapGet("/cameras/{id}", async (string id, ICameraManagementService service, CancellationToken token) =>
            await service.GetAsync(id, token) is { } camera ? Results.Ok(camera) : Results.NotFound());
        api.MapPost("/cameras", async (CameraWriteRequest request, ICameraManagementService service, CancellationToken token) =>
            Results.Created($"/api/v1/cameras/{request.Id}", await service.CreateAsync(request, token)));
        api.MapPut("/cameras/{id}", async (string id, CameraWriteRequest request, ICameraManagementService service, CancellationToken token) =>
            await service.UpdateAsync(id, request, token) is { } camera ? Results.Ok(camera) : Results.NotFound());
        api.MapDelete("/cameras/{id}", async (string id, ICameraManagementService service, CancellationToken token) =>
            await service.DeleteAsync(id, token) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/cameras/{id}/start", async (string id, ICameraManagementService service, CancellationToken token) =>
            await service.StartAsync(id, token) is { } camera ? Results.Ok(camera) : Results.NotFound());
        api.MapPost("/cameras/{id}/stop", async (string id, ICameraManagementService service, CancellationToken token) =>
            await service.StopAsync(id, token) is { } camera ? Results.Ok(camera) : Results.NotFound());
        api.MapPost("/cameras/test", async (CameraTestRequest request, ICameraManagementService service, CancellationToken token) =>
            Results.Ok(await service.TestAsync(request, token)));
        api.MapGet("/cameras/{id}/snapshot", async (string id, ICameraManagementService service, CancellationToken token) =>
            await service.GetSnapshotAsync(id, token) is { } jpeg
                ? Results.File(jpeg, "image/jpeg")
                : Results.Problem(statusCode: 404, title: "Snapshot no disponible"));
        api.MapGet("/cameras/{id}/stream", async (
            string id,
            double? fps,
            int? quality,
            ICameraManagementService service,
            Configuration.HostOptions options,
            HttpContext context,
            CancellationToken token) =>
        {
            var targetFps = Math.Clamp(fps ?? options.Streaming.DefaultStreamFps, 1.0, 60.0);
            var targetQuality = Math.Clamp(quality ?? options.Streaming.DefaultStreamQuality, 1, 100);

            var result = await service.SubscribeStreamAsync(id, targetFps, targetQuality, token);
            if (result.Status == CameraStreamStatus.NotFound)
            {
                return Results.NotFound();
            }
            if (result.Status == CameraStreamStatus.Offline)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Cámara no iniciada o sin frames", detail: result.Error);
            }
            if (result.Status == CameraStreamStatus.LimitReached)
            {
                return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Límite de clientes de streaming alcanzado", detail: result.Error);
            }

            if (result.Subscription is null || result.Reader is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Error al iniciar stream");
            }

            await using (result.Subscription)
            {
                context.Response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
                context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";

                var boundary = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: ");
                var headerEnd = Encoding.ASCII.GetBytes("\r\n\r\n");
                var crlf = Encoding.ASCII.GetBytes("\r\n");

                try
                {
                    await foreach (var jpeg in result.Reader.ReadAllAsync(token))
                    {
                        await context.Response.Body.WriteAsync(boundary, token);
                        await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(jpeg.Length.ToString()), token);
                        await context.Response.Body.WriteAsync(headerEnd, token);
                        await context.Response.Body.WriteAsync(jpeg, token);
                        await context.Response.Body.WriteAsync(crlf, token);
                        await context.Response.Body.FlushAsync(token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
            }

            return Results.Empty;
        });
        api.MapGet("/ws/events", async (
            HttpContext context,
            HandEventBus eventBus,
            Configuration.HostOptions options,
            string? camera_id,
            string? camera,
            CancellationToken token) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest("Se requiere conexión WebSocket.");
            }

            var targetCameraId = !string.IsNullOrWhiteSpace(camera_id) ? camera_id.Trim()
                : !string.IsNullOrWhiteSpace(camera) ? camera.Trim()
                : null;

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await using var subscription = eventBus.Subscribe();

            var capacity = Math.Max(16, options.Streaming.EventChannelCapacity);
            var clientChannel = Channel.CreateBounded<HandEvent>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true
            });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            var readerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var handEvent in subscription.Reader.ReadAllAsync(cts.Token))
                    {
                        if (targetCameraId is null || string.Equals(handEvent.CameraId, targetCameraId, StringComparison.OrdinalIgnoreCase))
                        {
                            clientChannel.Writer.TryWrite(handEvent);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    clientChannel.Writer.TryComplete();
                }
            }, cts.Token);

            var writerTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var handEvent in clientChannel.Reader.ReadAllAsync(cts.Token))
                    {
                        if (webSocket.State != WebSocketState.Open) break;
                        var json = JsonSerializer.SerializeToUtf8Bytes(handEvent);
                        await webSocket.SendAsync(new ArraySegment<byte>(json), WebSocketMessageType.Text, true, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);

            var buffer = new byte[256];
            try
            {
                while (webSocket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                {
                    var wsResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (wsResult.MessageType == WebSocketMessageType.Close)
                    {
                        if (webSocket.State == WebSocketState.CloseReceived)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                cts.Cancel();
                try { await Task.WhenAll(readerTask, writerTask); } catch { }
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                    }
                    catch { }
                }
            }

            return Results.Empty;
        });
        api.MapGet("/ws/metrics", async (
            HttpContext context,
            PipelineMetricsRegistry metrics,
            HostRuntimeState runtime,
            Configuration.HostOptions options,
            double? interval,
            double? interval_seconds,
            CancellationToken token) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest("Se requiere conexión WebSocket.");
            }

            var requestedInterval = interval_seconds ?? interval ?? options.Streaming.MetricsIntervalSeconds;
            var intervalSeconds = Math.Clamp(requestedInterval, 0.1, 60.0);
            var intervalTimeSpan = TimeSpan.FromSeconds(intervalSeconds);

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

            var senderTask = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(intervalTimeSpan);
                var initialSnapshot = new
                {
                    node_id = options.NodeId,
                    site_id = options.SiteId,
                    pipeline = metrics.Snapshot(),
                    active_device_id = runtime.ActiveDeviceId,
                    model = runtime.ModelName
                };
                if (webSocket.State == WebSocketState.Open)
                {
                    var firstBytes = JsonSerializer.SerializeToUtf8Bytes(initialSnapshot);
                    await webSocket.SendAsync(new ArraySegment<byte>(firstBytes), WebSocketMessageType.Text, true, cts.Token);
                }

                while (webSocket.State == WebSocketState.Open && await timer.WaitForNextTickAsync(cts.Token))
                {
                    var payload = new
                    {
                        node_id = options.NodeId,
                        site_id = options.SiteId,
                        pipeline = metrics.Snapshot(),
                        active_device_id = runtime.ActiveDeviceId,
                        model = runtime.ModelName
                    };
                    var json = JsonSerializer.SerializeToUtf8Bytes(payload);
                    await webSocket.SendAsync(new ArraySegment<byte>(json), WebSocketMessageType.Text, true, cts.Token);
                }
            }, cts.Token);

            var buffer = new byte[256];
            try
            {
                while (webSocket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                {
                    var wsResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (wsResult.MessageType == WebSocketMessageType.Close)
                    {
                        if (webSocket.State == WebSocketState.CloseReceived)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                cts.Cancel();
                try { await senderTask; } catch { }
                if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                    }
                    catch { }
                }
            }

            return Results.Empty;
        });
        api.MapGet("/cameras/{id}/zones", async (string id, ICameraManagementService service, CancellationToken token) =>
            await service.GetZonesAsync(id, token) is { } zones ? Results.Ok(zones) : Results.NotFound());
        api.MapPut("/cameras/{id}/zones", async (string id, HandRaise.Application.Zones.NormalizedZone[] zones,
            ICameraManagementService service, CancellationToken token) =>
            await service.UpdateZonesAsync(id, zones, token) ? Results.Ok(zones) : Results.NotFound());
        api.MapGet("/system/runtime", (HostRuntimeState runtime) => Results.Ok(runtime.Inventory.Runtime));
        api.MapGet("/system/capacity", (
            PipelineMetricsRegistry metrics,
            Configuration.HostOptions options) =>
            Results.Ok(metrics.Capacity(options.Capacity.TargetFramesPerSecond)));
        api.MapPut("/system/device", async (DeviceChangeRequest request, ICameraManagementService service,
            CancellationToken token) =>
        {
            var result = await service.ChangeDeviceAsync(request.DeviceId, token);
            return result.Success ? Results.Ok(result) : Results.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "No fue posible cambiar el dispositivo", detail: result.Error);
        });

        api.MapGet("/events", async (
            EventReadService service,
            string? camera,
            string? zone,
            string? type,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit = 100,
            int offset = 0,
            CancellationToken token = default) =>
            Results.Ok(await service.QueryAsync(
                new EventQuery(camera, zone, type, from, to, limit, offset), token)));

        api.MapGet("/events/{id}/snapshot", async (
            string id,
            EventReadService service,
            CancellationToken token) =>
        {
            var snapshot = await service.SnapshotAsync(id, token);
            return snapshot is null
                ? Results.Problem(statusCode: 404, title: "Snapshot no encontrado")
                : Results.File(snapshot.Path, snapshot.ContentType);
        });

        api.MapGet("/stats", async (
            EventReadService service,
            string? camera,
            string? zone,
            string? type,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken token) =>
            Results.Ok(await service.StatisticsAsync(
                new EventQuery(camera, zone, type, from, to, 1000, 0), token)));
    }
}
