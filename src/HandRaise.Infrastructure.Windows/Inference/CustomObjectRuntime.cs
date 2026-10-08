using HandRaise.Application.Analytics;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using HandRaise.Infrastructure.Windows.Training;

namespace HandRaise.Infrastructure.Windows.Inference;

/// <summary>One owner per camera pipeline; caches sessions and executes each model once per captured frame.</summary>
public sealed class CustomObjectRuntime(IModelRegistry registry,
    Func<ModelDescriptor, CancellationToken, Task<IInferenceBackend>>? load = null) : ICustomObjectRuntime
{
    private readonly Dictionary<string, IInferenceBackend> _backends = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastEvent = [];

    public async Task<CustomFrameResult> ProcessAsync(string cameraId, ImageFrame frame, DateTimeOffset timestamp,
        IReadOnlyList<CustomObjectEvaluator> analytics, CancellationToken ct)
    {
        var detections = new List<ObjectDetection>(); var events = new List<AnalyticEvent>();
        var errors = new Dictionary<string, string>();
        var active = analytics.Select(a => a.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeInstances = analytics.Select(a => a.InstanceId).ToHashSet();
        foreach (var id in _lastEvent.Keys.Where(id => !activeInstances.Contains(id)).ToArray()) _lastEvent.Remove(id);
        foreach (var id in _backends.Keys.Where(id => !active.Contains(id)).ToArray())
        { await _backends[id].DisposeAsync(); _backends.Remove(id); }
        foreach (var id in _failures.Keys.Where(id => !active.Contains(id)).ToArray()) _failures.Remove(id);
        foreach (var group in analytics.GroupBy(a => a.ModelId, StringComparer.OrdinalIgnoreCase))
        {
            if (_failures.TryGetValue(group.Key, out var failure))
            {
                foreach (var analytic in group) errors[analytic.InstanceId] = failure;
                continue;
            }
            try
            {
                var model = registry.GetModel(group.Key);
                if (model?.CustomTraining == null || model.KeypointCount != 0)
                    throw new InvalidOperationException("VisionControl no pudo cargar este modelo. Seleccione una versión disponible.");
                if (!_backends.TryGetValue(group.Key, out var backend))
                {
                    backend = await (load ?? LoadAsync)(model, ct);
                    _backends.Add(group.Key, backend);
                }
                var batch = await backend.InferAsync(frame, ct);
                var objects = batch.People.Select(d =>
                {
                    if (d.ClassId < 0 || d.ClassId >= model.CustomTraining.Classes.Count)
                        throw new InvalidDataException("Clase incompatible con el modelo.");
                    var cls = model.CustomTraining.Classes[d.ClassId];
                    return new ObjectDetection(model.Id, model.DisplayName, model.Version, cls.Id, cls.Name, d.ClassId, d.Confidence, d.Box);
                }).ToArray();
                detections.AddRange(objects.Where(d => group.Any(a => d.Confidence >= a.Confidence)));
                foreach (var analytic in group)
                {
                    // Observational events, at most one batch per second per analytic; never creates rules or alerts.
                    if (_lastEvent.TryGetValue(analytic.InstanceId, out var last) && timestamp - last < TimeSpan.FromSeconds(1)) continue;
                    var matches = objects.Where(d => d.Confidence >= analytic.Confidence).ToArray();
                    if (matches.Length == 0) continue;
                    _lastEvent[analytic.InstanceId] = timestamp;
                    foreach (var d in matches)
                        events.Add(new(Guid.NewGuid().ToString("N"), cameraId, analytic.InstanceId, CustomObjectEvaluator.TypeId,
                            "custom_object_detected", timestamp, Confidence: d.Confidence,
                            Metadata: new Dictionary<string, object?> { ["model_id"] = d.ModelId, ["model_name"] = d.ModelName,
                                ["model_version"] = d.Version, ["class_id"] = d.ClassId, ["class_name"] = d.ClassName,
                                ["class_index"] = d.ClassIndex, ["bbox"] = new { left = d.Box.Left, top = d.Box.Top,
                                    right = d.Box.Right, bottom = d.Box.Bottom }, ["emit_snapshot"] = false }));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (_backends.Remove(group.Key, out var failedBackend)) await failedBackend.DisposeAsync();
                _failures[group.Key] = "VisionControl no pudo cargar este modelo. " + ex.Message;
                foreach (var analytic in group) errors[analytic.InstanceId] = _failures[group.Key];
            }
        }
        return new(detections, events, errors);
    }

    private static async Task<IInferenceBackend> LoadAsync(ModelDescriptor model, CancellationToken ct)
    {
        await new OnnxTrainingArtifactValidator().ValidateAsync(model.Path, model.InputWidth, model.CustomTraining!.Classes, ct).ConfigureAwait(false);
        await using (var stream = File.OpenRead(model.Path))
        {
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
            if (!string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La integridad del modelo no es válida.");
        }
        var backend = new WindowsMlOnnxBackend(new DeviceInfo("cpu", "CPU", HardwareVendor.Cpu, InferenceBackend.Cpu, null, null, null, true));
        try { await backend.LoadAsync(model with { ConfidenceThreshold = .01f }, ct); return backend; }
        catch { await backend.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    { foreach (var backend in _backends.Values) await backend.DisposeAsync(); _backends.Clear(); }
}
