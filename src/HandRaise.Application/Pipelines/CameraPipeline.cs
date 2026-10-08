using System.Diagnostics;
using System.Runtime.CompilerServices;
using HandRaise.Application.Analytics;
using HandRaise.Application.Capture;
using HandRaise.Application.Events;
using HandRaise.Application.Inference;
using HandRaise.Application.Lines;
using HandRaise.Application.Overlay;
using HandRaise.Application.Storage;
using HandRaise.Application.Tracking;
using HandRaise.Application.Zones;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Lines;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Pipelines;

public sealed class CameraPipeline
{
    private readonly string _cameraId;
    private readonly IVideoSource _source;
    private readonly IInferenceBackend _backend;
    private readonly IFrameOverlay _overlay;
    private readonly DetectionOptions _detectionOptions;
    private readonly IReadOnlyList<ZoneDefinition> _zones;
    private readonly IZoneProvider? _zoneProvider;
    private readonly IReadOnlyList<LineDefinition> _lines;
    private readonly ILineProvider? _lineProvider;
    private readonly string _modelName;
    private readonly IVideoFrameSink? _rawSink;
    private readonly ITracker _tracker;
    private readonly IReadOnlyList<IAnalyticEvaluator> _evaluators;
    private readonly IAnalyticEvaluatorProvider? _evaluatorProvider;
    private readonly IHandEventPublisher? _eventPublisher;
    private readonly Func<long>? _backendGeneration;
    private readonly IPersonSnapshotEncoder? _snapshotEncoder;
    private readonly SnapshotEncodingOptions? _snapshotOptions;
    private readonly Action<string>? _log;
    private readonly double _targetInferenceFps;
    private readonly ICustomObjectRuntime? _customRuntime;
    private readonly Action<IReadOnlyDictionary<string, string>>? _customErrors;

    public IReadOnlyList<IAnalyticEvaluator> Evaluators => _evaluatorProvider?.GetEvaluators() ?? _evaluators;

    public CameraPipeline(
        string cameraId,
        IVideoSource source,
        IInferenceBackend backend,
        IFrameOverlay overlay,
        DetectionOptions detectionOptions,
        IReadOnlyList<ZoneDefinition> zones,
        string modelName,
        ITracker tracker,
        IVideoFrameSink? rawSink = null,
        IHandEventPublisher? eventPublisher = null,
        Func<long>? backendGeneration = null,
        IPersonSnapshotEncoder? snapshotEncoder = null,
        SnapshotEncodingOptions? snapshotOptions = null,
        Action<string>? log = null,
        IZoneProvider? zoneProvider = null,
        IAnalyticEvaluator? evaluator = null,
        IReadOnlyList<IAnalyticEvaluator>? evaluators = null,
        IAnalyticEvaluatorProvider? evaluatorProvider = null,
        ILineProvider? lineProvider = null,
        IReadOnlyList<LineDefinition>? lines = null,
        double targetInferenceFps = 0,
        ICustomObjectRuntime? customRuntime = null,
        Action<IReadOnlyDictionary<string, string>>? customErrors = null)
    {
        _cameraId = cameraId;
        _source = source;
        _backend = backend;
        _overlay = overlay;
        _detectionOptions = detectionOptions;
        _zones = zones;
        _zoneProvider = zoneProvider;
        _lines = lines ?? [];
        _lineProvider = lineProvider;
        _modelName = modelName;
        _rawSink = rawSink;
        _tracker = tracker;
        _evaluatorProvider = evaluatorProvider;
        _targetInferenceFps = targetInferenceFps;
        _customRuntime = customRuntime;
        _customErrors = customErrors;

        if (evaluators != null)
        {
            _evaluators = evaluators;
        }
        else if (evaluator != null)
        {
            _evaluators = [evaluator];
        }
        else
        {
            _evaluators = [];
        }

        _eventPublisher = eventPublisher;
        _backendGeneration = backendGeneration;
        _snapshotEncoder = snapshotEncoder;
        _snapshotOptions = snapshotOptions;
        _snapshotOptions?.Validate();
        _log = log;
    }

    public async IAsyncEnumerable<ProcessedCameraFrame> RunAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        long processed = 0;
        long inferenceCount = 0;
        long inferenceDroppedCount = 0;
        var inferenceStarted = Stopwatch.GetTimestamp();
        double lastInferenceTimestamp = -1;
        PoseBatch? lastPose = null;
        IReadOnlyList<EvaluatedPerson> lastOverlayPeople = [];
        IReadOnlyList<ObjectDetection> objects = [];
        double lastEvalDurationMs = 0;
        int lastEvaluatorErrors = 0;
        var minInferenceIntervalMs = _targetInferenceFps > 0 ? 1000.0 / _targetInferenceFps : 0.0;

        var sourceGeneration = (_source as IConnectionGenerationSource)?.ConnectionGeneration ?? 0;
        var backendGeneration = _backendGeneration?.Invoke() ?? 0;

        await foreach (var captured in _source.ReadAllAsync(cancellationToken))
        {
            using (captured)
            {
                var currentSourceGeneration =
                    (_source as IConnectionGenerationSource)?.ConnectionGeneration ?? sourceGeneration;
                var currentBackendGeneration = _backendGeneration?.Invoke() ?? backendGeneration;

                var currentEvaluators = _evaluatorProvider?.GetEvaluators() ?? _evaluators;
                if (currentSourceGeneration != sourceGeneration || currentBackendGeneration != backendGeneration)
                {
                    _tracker.Reset();
                    foreach (var evaluator in currentEvaluators)
                    {
                        try
                        {
                            evaluator.Reset();
                        }
                        catch (Exception exception)
                        {
                            _log?.Invoke($"Error al reiniciar evaluador '{evaluator.AnalyticTypeId}' en cámara '{_cameraId}': {exception.Message}");
                        }
                    }
                    sourceGeneration = currentSourceGeneration;
                    backendGeneration = currentBackendGeneration;
                }

                if (_rawSink is not null)
                {
                    await _rawSink.WriteAsync(captured, cancellationToken);
                }

                var frameStarted = Stopwatch.GetTimestamp();
                var zones = _zoneProvider?.GetZones(captured.Width, captured.Height) ?? _zones;
                var lines = _lineProvider?.GetLines(_cameraId) ?? _lines;
                var activeEvaluators = currentEvaluators.Where(e => e.IsEnabled).ToList();

                if (activeEvaluators.Count == 0)
                {
                    objects = [];
                    if (_customRuntime != null)
                        await _customRuntime.ProcessAsync(_cameraId, captured.AsImageFrame(), captured.TimestampUtc, [], cancellationToken);
                    _customErrors?.Invoke(new Dictionary<string, string>());
                    processed++;
                    var elapsedSecs = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    var currentFps = elapsedSecs > 0 ? processed / elapsedSecs : 0;
                    var preliminaryTotalMs = Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;
                    var beforeOverlayPass = Stopwatch.GetTimestamp();

                    var cleanAnnotated = zones.Count > 0
                        ? _overlay.Draw(captured, [], zones, new OverlayStatistics(currentFps, preliminaryTotalMs, string.Empty, string.Empty, string.Empty))
                        : captured.Clone();

                    var overlayTimeSpan = Stopwatch.GetElapsedTime(beforeOverlayPass);
                    var totalTimeSpan = Stopwatch.GetElapsedTime(frameStarted);
                    var frameAge = Stopwatch.GetElapsedTime(captured.AcquiredAtStopwatchTicks).TotalMilliseconds;

                    var zeroMetrics = new CameraPipelineMetrics(
                        processed,
                        _source.DroppedFrames,
                        currentFps,
                        captured.DecodeMilliseconds,
                        frameAge,
                        PreprocessMilliseconds: 0,
                        InferenceMilliseconds: 0,
                        PostprocessMilliseconds: 0,
                        OverlayMilliseconds: overlayTimeSpan.TotalMilliseconds,
                        TotalMilliseconds: totalTimeSpan.TotalMilliseconds,
                        AnalyticDurationMilliseconds: 0,
                        EvaluatorErrors: 0,
                        ActiveAnalyticCount: 0,
                        InferenceFps: 0,
                        DroppedInferenceFrames: 0);

                    yield return new ProcessedCameraFrame(
                        _cameraId, cleanAnnotated, [], [], zeroMetrics);
                    continue;
                }

                var shouldInfer = lastInferenceTimestamp < 0 ||
                    minInferenceIntervalMs <= 0 ||
                    (captured.TimestampMilliseconds - lastInferenceTimestamp) >= minInferenceIntervalMs;

                PoseBatch pose;
                IReadOnlyList<EvaluatedPerson> overlayPeople;
                IReadOnlyList<HandEvent> emittedEvents;
                double evalDurationMs;
                int evaluatorErrors;
                var activeEvaluatorCount = activeEvaluators.Count;

                if (shouldInfer)
                {
                    lastInferenceTimestamp = captured.TimestampMilliseconds;
                    inferenceCount++;

                    // 1. Single inference execution per frame (only when active evaluators exist)
                    pose = activeEvaluators.Any(e => e is not CustomObjectEvaluator)
                        ? await _backend.InferAsync(captured.AsImageFrame(), cancellationToken)
                        : new PoseBatch([], TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
                    lastPose = pose;

                    // 2. Single tracking update per frame
                    var tracked = _tracker.Update(pose.People, captured.TimestampMilliseconds);

                    // 3. Build generic shared context
                    var context = new AnalyticFrameContext(
                        _cameraId,
                        captured.TimestampMilliseconds,
                        captured.TimestampUtc,
                        captured.Width,
                        captured.Height,
                        tracked,
                        zones,
                        lines);

                    // 4. Dispatch to all active evaluators with fault isolation
                    var allPeople = new List<EvaluatedPerson>();
                    var allLegacyEvents = new List<HandEvent>();
                    var evalStart = Stopwatch.GetTimestamp();
                    evaluatorErrors = 0;

                    if (_customRuntime != null)
                    {
                        var result = await _customRuntime.ProcessAsync(_cameraId, captured.AsImageFrame(), captured.TimestampUtc,
                            activeEvaluators.OfType<CustomObjectEvaluator>().ToArray(), cancellationToken);
                        objects = result.Detections;
                        _customErrors?.Invoke(result.Errors);
                        evaluatorErrors += result.Errors.Count;
                        foreach (var ev in result.Events)
                            allLegacyEvents.Add(AnalyticEventAdapter.ToLegacyHandEvent(ev) with { SnapshotJpeg = [] });
                    }

                    foreach (var evaluator in activeEvaluators.Where(e => e is not CustomObjectEvaluator))
                    {
                        try
                        {
                            var evalResult = evaluator.Evaluate(context);
                            if (evalResult.People.Count > 0)
                            {
                                allPeople.AddRange(evalResult.People);
                            }
                            if (evalResult.LegacyEvents.Count > 0)
                            {
                                allLegacyEvents.AddRange(evalResult.LegacyEvents);
                            }
                            else if (evalResult.Events.Count > 0)
                            {
                                foreach (var genericEvent in evalResult.Events)
                                {
                                    var legacyEvent = AnalyticEventAdapter.ToLegacyHandEvent(genericEvent);
                                    if (genericEvent.Metadata != null &&
                                        genericEvent.Metadata.TryGetValue("emit_snapshot", out var emitSnap) &&
                                        emitSnap is false or (object)false)
                                    {
                                        legacyEvent = legacyEvent with { SnapshotJpeg = [] };
                                    }
                                    allLegacyEvents.Add(legacyEvent);
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            evaluatorErrors++;
                            _log?.Invoke($"Error en evaluador '{evaluator.AnalyticTypeId}' (Instancia: '{evaluator.InstanceId}') en cámara '{_cameraId}': {exception.Message}");
                        }
                    }

                    evalDurationMs = Stopwatch.GetElapsedTime(evalStart).TotalMilliseconds;
                    lastEvalDurationMs = evalDurationMs;
                    lastEvaluatorErrors = evaluatorErrors;

                    // Deduplicate people by TrackId/Detection for overlay rendering
                    overlayPeople = DeduplicatePeople(allPeople, tracked);
                    lastOverlayPeople = overlayPeople;

                    emittedEvents = allLegacyEvents.Select(handEvent =>
                        AddSnapshot(handEvent, overlayPeople, captured)).ToArray();

                    if (_eventPublisher is not null)
                    {
                        foreach (var handEvent in emittedEvents)
                        {
                            await _eventPublisher.PublishAsync(handEvent, cancellationToken);
                        }
                    }
                }
                else
                {
                    inferenceDroppedCount++;
                    pose = lastPose ?? new PoseBatch([], TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
                    overlayPeople = lastOverlayPeople;
                    emittedEvents = [];
                    evalDurationMs = lastEvalDurationMs;
                    evaluatorErrors = lastEvaluatorErrors;
                }

                processed++;
                var elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                var fps = elapsedSeconds > 0 ? processed / elapsedSeconds : 0;
                var inferenceElapsedSecs = Stopwatch.GetElapsedTime(inferenceStarted).TotalSeconds;
                var inferenceFps = inferenceElapsedSecs > 0 ? inferenceCount / inferenceElapsedSecs : 0;
                var beforeOverlay = Stopwatch.GetTimestamp();
                var preliminaryTotal = Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;

                var statistics = new OverlayStatistics(
                    fps,
                    preliminaryTotal,
                    _backend.Device.Name,
                    _backend.ExecutionInfo.ProviderName,
                    activeEvaluators.All(e => e is CustomObjectEvaluator)
                        ? "Detección personalizada (CPU)" : _modelName);

                var annotated = _overlay.Draw(captured, overlayPeople, zones, statistics);
                if (objects.Count > 0 && _overlay is IObjectFrameOverlay objectOverlay)
                {
                    using var original = annotated;
                    annotated = objectOverlay.DrawObjects(original, objects);
                }
                var overlayTime = Stopwatch.GetElapsedTime(beforeOverlay);
                var totalTime = Stopwatch.GetElapsedTime(frameStarted);
                var age = Stopwatch.GetElapsedTime(captured.AcquiredAtStopwatchTicks).TotalMilliseconds;

                var metrics = new CameraPipelineMetrics(
                    processed,
                    _source.DroppedFrames,
                    fps,
                    captured.DecodeMilliseconds,
                    age,
                    pose.PreprocessLatency.TotalMilliseconds,
                    pose.InferenceLatency.TotalMilliseconds,
                    pose.PostprocessLatency.TotalMilliseconds,
                    overlayTime.TotalMilliseconds,
                    totalTime.TotalMilliseconds,
                    AnalyticDurationMilliseconds: evalDurationMs,
                    EvaluatorErrors: evaluatorErrors,
                    ActiveAnalyticCount: activeEvaluatorCount,
                    InferenceFps: inferenceFps,
                    DroppedInferenceFrames: inferenceDroppedCount);

                yield return new ProcessedCameraFrame(
                    _cameraId, annotated, overlayPeople, emittedEvents, metrics) { Objects = objects };
            }
        }
    }

    private static IReadOnlyList<EvaluatedPerson> DeduplicatePeople(
        List<EvaluatedPerson> people,
        TrackerUpdate trackerUpdate)
    {
        if (people.Count == 0)
        {
            // Default empty evaluation for detected people
            return trackerUpdate.People.Select(p =>
                new EvaluatedPerson(p.Detection, new HandsState(false, false, 0.0), null, p.TrackId, null)).ToArray();
        }

        var seenTracks = new HashSet<int>();
        var result = new List<EvaluatedPerson>(people.Count);

        foreach (var person in people)
        {
            if (person.TrackId is { } trackId)
            {
                if (seenTracks.Add(trackId))
                {
                    result.Add(person);
                }
            }
            else
            {
                result.Add(person);
            }
        }

        return result;
    }

    private HandEvent AddSnapshot(
        HandEvent handEvent,
        IReadOnlyList<EvaluatedPerson> people,
        VideoFrame frame)
    {
        if (handEvent.SnapshotJpeg is not null)
        {
            return handEvent.SnapshotJpeg.Length == 0 ? handEvent with { SnapshotJpeg = null } : handEvent;
        }

        if (handEvent.Type != "hand_raised" &&
            handEvent.Type != "person_presence_started" &&
            handEvent.Type != "zone_intrusion_started" &&
            handEvent.Type != "line_crossed")
        {
            return handEvent;
        }

        if (_snapshotEncoder is null || _snapshotOptions is null)
        {
            return handEvent;
        }

        var person = people.FirstOrDefault(item => item.TrackId == handEvent.TrackId);
        if (person is null && people.Count > 0)
        {
            person = people[0];
        }

        if (person is null)
        {
            return handEvent;
        }

        try
        {
            return handEvent with
            {
                SnapshotJpeg = _snapshotEncoder.Encode(frame, person.Detection.Box, _snapshotOptions)
            };
        }
        catch (Exception exception)
        {
            _log?.Invoke($"No fue posible crear el snapshot del evento {handEvent.Id}: {exception.Message}");
            return handEvent;
        }
    }
}
