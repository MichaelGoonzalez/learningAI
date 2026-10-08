using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HandRaise.Application.Analytics;
using HandRaise.Application.Events;
using HandRaise.Application.Inference;
using HandRaise.Application.Notifications;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Rules;
using HandRaise.Application.Storage;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using HandRaise.Domain.Notifications;
using HandRaise.Domain.Rules;
using HandRaise.Host.Api;
using HandRaise.Host.Configuration;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Inference;
using HandRaise.Infrastructure.Windows.Notifications;
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
        if (!builder.Services.Any(item => item.ServiceType == typeof(IAnalyticCatalog)))
            builder.Services.AddSingleton<IAnalyticCatalog, StandardAnalyticCatalog>();
        if (!builder.Services.Any(item => item.ServiceType == typeof(IModelRegistry)))
        {
            builder.Services.AddSingleton<IModelRegistry>(_ =>
            {
                var registry = new StandardModelRegistry(new HandRaise.Infrastructure.Windows.Training.TrainingPaths().CustomModels);
                var defaultModel = new ModelDescriptor(
                    Id: "yolo26n-pose",
                    DisplayName: "YOLO26 Nano Pose",
                    Version: "1.0.0",
                    Format: ModelFormat.Onnx,
                    Capabilities: [InferenceCapability.PoseEstimation, InferenceCapability.ObjectDetection, InferenceCapability.Tracking],
                    InputWidth: options.Model.InputWidth,
                    InputHeight: options.Model.InputHeight,
                    Path: options.Model.Path,
                    ClassCount: options.Model.ClassCount,
                    KeypointCount: options.Model.KeypointCount,
                    ConfidenceThreshold: options.Model.ConfidenceThreshold,
                    IouThreshold: options.Model.IouThreshold,
                    MaximumDetections: options.Model.MaximumDetections,
                    PreferredDevice: "gpu",
                    MemoryEstimateMb: 150.0);

                var defaultProvider = new YoloPoseCapabilityProvider(
                    providerId: "yolo-pose-provider",
                    modelId: "yolo26n-pose",
                    modelVersion: "1.0.0",
                    capabilities: [InferenceCapability.PoseEstimation, InferenceCapability.ObjectDetection, InferenceCapability.Tracking],
                    preferredDevice: "gpu");

                registry.RegisterModel(defaultModel, defaultProvider);
                return registry;
            });
        }
        if (!builder.Services.Any(item => item.ServiceType == typeof(ICapabilityPlanner)))
            builder.Services.AddSingleton<ICapabilityPlanner, CapabilityPlanner>();
        if (!builder.Services.Any(item => item.ServiceType == typeof(IAnalyticEvaluatorFactory)))
            builder.Services.AddSingleton<IAnalyticEvaluatorFactory, AnalyticEvaluatorFactory>();
        if (!builder.Services.Any(item => item.ServiceType == typeof(IAnalyticInstanceStore)))
            builder.Services.AddSingleton<IAnalyticInstanceStore>(_ => new JsonAnalyticInstanceStore(options.Storage.AnalyticStorePath));
        if (!builder.Services.Any(item => item.ServiceType == typeof(IAnalyticManagementService)))
            builder.Services.AddSingleton<IAnalyticManagementService, AnalyticManagementService>();
        if (!builder.Services.Any(item => item.ServiceType == typeof(HandRaise.Application.Lines.ILineStore)))
            builder.Services.AddSingleton<HandRaise.Application.Lines.ILineStore>(_ => new JsonLineStore(options.Storage.LinesStorePath));
        if (!builder.Services.Any(item => item.ServiceType == typeof(IRuleStore)))
            builder.Services.AddSingleton<IRuleStore>(_ => new JsonRuleStore(options.Storage.RulesStorePath));
        if (!builder.Services.Any(item => item.ServiceType == typeof(IRuleProvider)))
            builder.Services.AddSingleton<IRuleProvider>(sp => sp.GetRequiredService<IRuleStore>());
        if (!builder.Services.Any(item => item.ServiceType == typeof(IAlertStore)))
            builder.Services.AddSingleton<IAlertStore>(_ => new JsonAlertStore(options.Storage.AlertsStorePath));
        if (!builder.Services.Any(item => item.ServiceType == typeof(IRuleEngine)))
            builder.Services.AddSingleton<IRuleEngine>(sp => new RuleEngine(
                sp.GetRequiredService<IRuleProvider>(),
                sp.GetRequiredService<IAlertStore>()));
        if (!builder.Services.Any(item => item.ServiceType == typeof(INotificationDestinationStore)))
            builder.Services.AddSingleton<INotificationDestinationStore>(_ => new JsonNotificationDestinationStore(options.Storage.NotificationDestinationsStorePath));
        if (!builder.Services.Any(item => item.ServiceType == typeof(INotificationPolicyStore)))
            builder.Services.AddSingleton<INotificationPolicyStore>(_ => new JsonNotificationPolicyStore(options.Storage.NotificationPoliciesStorePath));
        if (!builder.Services.Any(item => item.ServiceType == typeof(INotificationAttemptStore)))
            builder.Services.AddSingleton<INotificationAttemptStore>(_ => new JsonNotificationAttemptStore(options.Storage.NotificationAttemptsStorePath));
        if (!builder.Services.Any(item => item.ServiceType == typeof(INotificationDispatcher)))
        {
            builder.Services.AddSingleton<INotificationDispatcher>(sp =>
            {
                var senders = new INotificationSender[]
                {
                    new HttpWebhookNotificationSender(
                        httpClient: null,
                        allowInsecureHttp: options.Notifications.AllowInsecureHttpWebhooks,
                        enableSsrfCheck: options.Notifications.EnableSsrfProtection),
                    new MqttNotificationSender()
                };
                return new NotificationDispatcher(
                    sp.GetRequiredService<INotificationPolicyStore>(),
                    sp.GetRequiredService<INotificationDestinationStore>(),
                    sp.GetRequiredService<INotificationAttemptStore>(),
                    senders,
                    options.NodeId,
                    options.SiteId);
            });
        }
        builder.Services.AddHostedService<AlertEngineWorker>();
        if (!builder.Services.Any(item => item.ServiceType == typeof(ICameraStore)))
            builder.Services.AddSingleton<ICameraStore>(_ => new JsonCameraStore(options.Storage.CameraStorePath));
        if (!builder.Services.Any(item => item.ServiceType == typeof(ICameraCredentialStore)))
            builder.Services.AddSingleton<ICameraCredentialStore>(_ => new JsonCameraCredentialStore(
                options.Storage.CameraCredentialStorePath, new MachineDpapiProtector()));
        if (!builder.Services.Any(item => item.ServiceType == typeof(INodeCredentialStore)))
            builder.Services.AddSingleton<INodeCredentialStore>(_ => new JsonNodeCredentialStore(
                options.Storage.NodeCredentialStorePath, new MachineDpapiProtector()));
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
            policy.SetIsOriginAllowed(origin => CorsOriginValidator.IsAllowedOrigin(origin, options.Api.AllowedOrigins))
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                .WithHeaders("Content-Type", "Accept", "X-Api-Key", options.Api.ApiKeyHeader);
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

        api.MapGet("/analytics/catalog", (IAnalyticManagementService service) =>
            Results.Ok(service.GetCatalog()));

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

        api.MapGet("/cameras/{cameraId}/analytics", async (
            string cameraId, IAnalyticManagementService service, CancellationToken token) =>
            await service.ListByCameraAsync(cameraId, token) is { } items
                ? Results.Ok(items)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Cámara no encontrada", detail: $"La cámara '{cameraId}' no existe."));

        api.MapPost("/cameras/{cameraId}/analytics", async (
            string cameraId, CameraAnalyticWriteRequest request, IAnalyticManagementService service, CancellationToken token) =>
        {
            try
            {
                var created = await service.CreateAsync(cameraId, request, token);
                return Results.Created($"/api/v1/cameras/{cameraId}/analytics/{created.Id}", created);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Cámara no encontrada", detail: ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflicto al crear analítica", detail: ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Parámetros inválidos", detail: ex.Message);
            }
        });

        api.MapGet("/cameras/{cameraId}/analytics/{instanceId}", async (
            string cameraId, string instanceId, IAnalyticManagementService service, CancellationToken token) =>
            await service.GetAsync(cameraId, instanceId, token) is { } instance
                ? Results.Ok(instance)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Instancia analítica no encontrada"));

        api.MapPut("/cameras/{cameraId}/analytics/{instanceId}", async (
            string cameraId, string instanceId, CameraAnalyticWriteRequest request, IAnalyticManagementService service, CancellationToken token) =>
        {
            try
            {
                var updated = await service.UpdateAsync(cameraId, instanceId, request, token);
                return updated is not null
                    ? Results.Ok(updated)
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Instancia analítica no encontrada");
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Parámetros inválidos", detail: ex.Message);
            }
        });

        api.MapDelete("/cameras/{cameraId}/analytics/{instanceId}", async (
            string cameraId, string instanceId, IAnalyticManagementService service, CancellationToken token) =>
            await service.DeleteAsync(cameraId, instanceId, token)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Instancia analítica no encontrada"));

        api.MapGet("/cameras/{cameraId}/lines", async (
            string cameraId, ICameraStore cameraStore, HandRaise.Application.Lines.ILineStore lineStore, CancellationToken token) =>
        {
            if (await cameraStore.GetAsync(cameraId, token) is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Cámara no encontrada", detail: $"La cámara '{cameraId}' no existe.");
            return Results.Ok(await lineStore.ListByCameraAsync(cameraId, token));
        });

        api.MapPost("/cameras/{cameraId}/lines", async (
            string cameraId, HandRaise.Domain.Lines.LineDefinition line, ICameraStore cameraStore, HandRaise.Application.Lines.ILineStore lineStore, CancellationToken token) =>
        {
            if (await cameraStore.GetAsync(cameraId, token) is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Cámara no encontrada", detail: $"La cámara '{cameraId}' no existe.");
            try
            {
                var adjusted = line with { CameraId = cameraId };
                adjusted.Validate();
                var saved = await lineStore.SaveAsync(cameraId, adjusted, token);
                return Results.Created($"/api/v1/cameras/{cameraId}/lines/{saved.Id}", saved);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Línea inválida", detail: ex.Message);
            }
        });

        api.MapPut("/cameras/{cameraId}/lines/{lineId}", async (
            string cameraId, string lineId, HandRaise.Domain.Lines.LineDefinition line, ICameraStore cameraStore, HandRaise.Application.Lines.ILineStore lineStore, CancellationToken token) =>
        {
            if (await cameraStore.GetAsync(cameraId, token) is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Cámara no encontrada", detail: $"La cámara '{cameraId}' no existe.");
            try
            {
                var adjusted = line with { Id = lineId, CameraId = cameraId };
                adjusted.Validate();
                var saved = await lineStore.SaveAsync(cameraId, adjusted, token);
                return Results.Ok(saved);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Línea inválida", detail: ex.Message);
            }
        });

        api.MapPut("/cameras/{cameraId}/lines", async (
            string cameraId, IReadOnlyList<HandRaise.Domain.Lines.LineDefinition> lines, ICameraStore cameraStore, HandRaise.Application.Lines.ILineStore lineStore, CancellationToken token) =>
        {
            if (await cameraStore.GetAsync(cameraId, token) is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Cámara no encontrada", detail: $"La cámara '{cameraId}' no existe.");
            try
            {
                var adjustedList = lines.Select(l => l with { CameraId = cameraId }).ToArray();
                foreach (var line in adjustedList) line.Validate();
                await lineStore.SaveAllAsync(cameraId, adjustedList, token);
                return Results.Ok(adjustedList);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Líneas inválidas", detail: ex.Message);
            }
        });

        api.MapDelete("/cameras/{cameraId}/lines/{lineId}", async (
            string cameraId, string lineId, ICameraStore cameraStore, HandRaise.Application.Lines.ILineStore lineStore, CancellationToken token) =>
        {
            if (await cameraStore.GetAsync(cameraId, token) is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Cámara no encontrada", detail: $"La cámara '{cameraId}' no existe.");
            return await lineStore.DeleteAsync(cameraId, lineId, token)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Línea no encontrada");
        });

        // Rules CRUD endpoints
        api.MapGet("/cameras/{cameraId}/analytics/{instanceId}/rules", async (
            string cameraId, string instanceId, IRuleStore ruleStore, CancellationToken token) =>
            Results.Ok(await ruleStore.ListByInstanceAsync(cameraId, instanceId, token)));

        api.MapPost("/cameras/{cameraId}/analytics/{instanceId}/rules", async (
            string cameraId, string instanceId, AlertRule rule, IRuleStore ruleStore, CancellationToken token) =>
        {
            try
            {
                var adjusted = rule with { CameraId = cameraId, AnalyticInstanceId = instanceId };
                adjusted.Validate();
                var saved = await ruleStore.SaveAsync(adjusted, token);
                return Results.Created($"/api/v1/cameras/{cameraId}/analytics/{instanceId}/rules/{saved.Id}", saved);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Regla inválida", detail: ex.Message);
            }
        });

        api.MapGet("/cameras/{cameraId}/analytics/{instanceId}/rules/{ruleId}", async (
            string cameraId, string instanceId, string ruleId, IRuleStore ruleStore, CancellationToken token) =>
            await ruleStore.GetByIdAsync(cameraId, instanceId, ruleId, token) is { } foundRule
                ? Results.Ok(foundRule)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Regla no encontrada"));

        api.MapPut("/cameras/{cameraId}/analytics/{instanceId}/rules/{ruleId}", async (
            string cameraId, string instanceId, string ruleId, AlertRule rule, IRuleStore ruleStore, CancellationToken token) =>
        {
            try
            {
                var adjusted = rule with { Id = ruleId, CameraId = cameraId, AnalyticInstanceId = instanceId };
                adjusted.Validate();
                var saved = await ruleStore.SaveAsync(adjusted, token);
                return Results.Ok(saved);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Regla inválida", detail: ex.Message);
            }
        });

        api.MapDelete("/cameras/{cameraId}/analytics/{instanceId}/rules/{ruleId}", async (
            string cameraId, string instanceId, string ruleId, IRuleStore ruleStore, CancellationToken token) =>
            await ruleStore.DeleteAsync(cameraId, instanceId, ruleId, token)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Regla no encontrada"));
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
        api.MapGet("/system/capacity", async (
            PipelineMetricsRegistry metrics,
            Configuration.HostOptions options,
            IModelRegistry registry,
            IAnalyticInstanceStore analyticStore,
            HostRuntimeState runtime,
            CancellationToken token) =>
        {
            var activeModels = registry.ListModels();
            var allInstances = await analyticStore.GetAllAsync(token);
            var countsByCamera = allInstances
                .Where(i => i.Enabled && i.Status == AnalyticStatus.Active)
                .GroupBy(i => i.CameraId)
                .ToDictionary(g => g.Key, g => g.Count());

            var extended = metrics.ExtendedCapacity(
                options.Capacity.TargetFramesPerSecond,
                deviceName: runtime.ActiveDeviceId ?? "D3D12 GPU",
                activeModels: activeModels,
                cameraAnalyticsCounts: countsByCamera);

            return Results.Ok(extended);
        });
        api.MapGet("/models", (IModelRegistry registry) => Results.Ok(registry.ListModels()));
        api.MapGet("/models/{id}", (string id, IModelRegistry registry) =>
            registry.GetModel(id) is { } model
                ? Results.Ok(model)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Modelo no encontrado"));
        api.MapGet("/cameras/{id}/runtime-plan", async (
            string id,
            ICapabilityPlanner planner,
            IAnalyticInstanceStore analyticStore,
            IAnalyticCatalog catalog,
            ICameraManagementService cameraService,
            CancellationToken token) =>
        {
            var camera = await cameraService.GetAsync(id, token);
            if (camera is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Cámara no encontrada");
            }

            var instances = await analyticStore.GetByCameraIdAsync(id, token);
            var plan = planner.CreatePlan(id, instances, catalog);
            return Results.Ok(plan);
        });
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

        // Alerts endpoints
        api.MapGet("/alerts", async (
            IAlertStore alertStore,
            string? camera_id,
            string? analytic_instance_id,
            string? severity,
            string? status,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int limit = 100,
            int offset = 0,
            CancellationToken token = default) =>
        {
            AlertSeverity? parsedSeverity = null;
            if (!string.IsNullOrWhiteSpace(severity) && Enum.TryParse<AlertSeverity>(severity, ignoreCase: true, out var sev))
            {
                parsedSeverity = sev;
            }

            AlertStatus? parsedStatus = null;
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<AlertStatus>(status, ignoreCase: true, out var st))
            {
                parsedStatus = st;
            }

            var query = new AlertQuery(
                CameraId: camera_id,
                AnalyticInstanceId: analytic_instance_id,
                Severity: parsedSeverity,
                Status: parsedStatus,
                From: from,
                To: to,
                Limit: limit,
                Offset: offset);

            var items = await alertStore.QueryAsync(query, token);
            return Results.Ok(items);
        });

        api.MapGet("/alerts/{alertId}", async (
            string alertId, IAlertStore alertStore, CancellationToken token) =>
            await alertStore.GetByIdAsync(alertId, token) is { } alert
                ? Results.Ok(alert)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Alerta no encontrada"));

        api.MapPost("/alerts/{alertId}/acknowledge", async (
            string alertId, IAlertStore alertStore, CancellationToken token) =>
            await alertStore.AcknowledgeAsync(alertId, DateTimeOffset.UtcNow, token) is { } alert
                ? Results.Ok(alert)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Alerta no encontrada"));

        api.MapPost("/alerts/{alertId}/resolve", async (
            string alertId, IAlertStore alertStore, CancellationToken token) =>
            await alertStore.ResolveAsync(alertId, DateTimeOffset.UtcNow, token) is { } alert
                ? Results.Ok(alert)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Alerta no encontrada"));

        // Notification Destinations CRUD endpoints
        api.MapGet("/notifications/destinations", async (
            INotificationDestinationStore destinationStore, CancellationToken token) =>
        {
            var items = await destinationStore.ListAllAsync(token);
            return Results.Ok(items.Select(JsonNotificationDestinationStore.Redact));
        });

        api.MapPost("/notifications/destinations", async (
            NotificationDestination destination, INotificationDestinationStore destinationStore, CancellationToken token) =>
        {
            try
            {
                destination.Validate();
                var saved = await destinationStore.SaveAsync(destination, token);
                return Results.Created($"/api/v1/notifications/destinations/{saved.Id}", JsonNotificationDestinationStore.Redact(saved));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Destino inválido", detail: ex.Message);
            }
        });

        api.MapGet("/notifications/destinations/{id}", async (
            string id, INotificationDestinationStore destinationStore, CancellationToken token) =>
            await destinationStore.GetByIdAsync(id, token) is { } dest
                ? Results.Ok(JsonNotificationDestinationStore.Redact(dest))
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Destino no encontrado"));

        api.MapPut("/notifications/destinations/{id}", async (
            string id, NotificationDestination destination, INotificationDestinationStore destinationStore, CancellationToken token) =>
        {
            try
            {
                var existing = await destinationStore.GetByIdAsync(id, token);
                if (existing is null)
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Destino no encontrado");

                var adjusted = destination with { Id = id };
                adjusted.Validate();
                var saved = await destinationStore.SaveAsync(adjusted, token);
                return Results.Ok(JsonNotificationDestinationStore.Redact(saved));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Destino inválido", detail: ex.Message);
            }
        });

        api.MapDelete("/notifications/destinations/{id}", async (
            string id, INotificationDestinationStore destinationStore, CancellationToken token) =>
            await destinationStore.DeleteAsync(id, token)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Destino no encontrado"));

        api.MapPost("/notifications/destinations/{id}/test", async (
            string id, INotificationDestinationStore destinationStore, INotificationDispatcher dispatcher, CancellationToken token) =>
        {
            var destination = await destinationStore.GetByIdAsync(id, token);
            if (destination is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Destino no encontrado");

            var attempt = await dispatcher.TestDestinationAsync(destination, token);
            return Results.Ok(attempt);
        });

        // Notification Policies CRUD endpoints
        api.MapGet("/notifications/policies", async (
            INotificationPolicyStore policyStore, CancellationToken token) =>
            Results.Ok(await policyStore.ListAllAsync(token)));

        api.MapPost("/notifications/policies", async (
            NotificationPolicy policy, INotificationPolicyStore policyStore, CancellationToken token) =>
        {
            try
            {
                policy.Validate();
                var saved = await policyStore.SaveAsync(policy, token);
                return Results.Created($"/api/v1/notifications/policies/{saved.Id}", saved);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Política inválida", detail: ex.Message);
            }
        });

        api.MapGet("/notifications/policies/{id}", async (
            string id, INotificationPolicyStore policyStore, CancellationToken token) =>
            await policyStore.GetByIdAsync(id, token) is { } p
                ? Results.Ok(p)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Política no encontrada"));

        api.MapPut("/notifications/policies/{id}", async (
            string id, NotificationPolicy policy, INotificationPolicyStore policyStore, CancellationToken token) =>
        {
            try
            {
                var existing = await policyStore.GetByIdAsync(id, token);
                if (existing is null)
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Política no encontrada");

                var adjusted = policy with { Id = id };
                adjusted.Validate();
                var saved = await policyStore.SaveAsync(adjusted, token);
                return Results.Ok(saved);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Política inválida", detail: ex.Message);
            }
        });

        api.MapDelete("/notifications/policies/{id}", async (
            string id, INotificationPolicyStore policyStore, CancellationToken token) =>
            await policyStore.DeleteAsync(id, token)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Política no encontrada"));

        // Notification Attempts query endpoint
        api.MapGet("/notifications/attempts", async (
            INotificationAttemptStore attemptStore,
            string? alert_id,
            string? destination_id,
            string? status,
            int limit = 100,
            int offset = 0,
            CancellationToken token = default) =>
        {
            NotificationStatus? parsedStatus = null;
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<NotificationStatus>(status, ignoreCase: true, out var st))
            {
                parsedStatus = st;
            }

            var query = new NotificationAttemptQuery(
                AlertId: alert_id,
                DestinationId: destination_id,
                Status: parsedStatus,
                Limit: limit,
                Offset: offset);

            var items = await attemptStore.QueryAsync(query, token);
            return Results.Ok(items);
        });
    }
}
