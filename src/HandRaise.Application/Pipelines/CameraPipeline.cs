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
        IReadOnlyList<LineDefinition>? lines = null)
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
            _evaluators = [new HandRaiseAnalyticEvaluator(detectionOptions)];
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

                // 1. Single inference execution per frame
                var pose = await _backend.InferAsync(captured.AsImageFrame(), cancellationToken);

                // 2. Single tracking update per frame
                var tracked = _tracker.Update(pose.People, captured.TimestampMilliseconds);
                var zones = _zoneProvider?.GetZones(captured.Width, captured.Height) ?? _zones;
                var lines = _lineProvider?.GetLines(_cameraId) ?? _lines;

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
                var evaluatorErrors = 0;
                var activeEvaluatorCount = 0;

                foreach (var evaluator in currentEvaluators)
                {
                    if (!evaluator.IsEnabled) continue;
                    activeEvaluatorCount++;

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
                            // Adapt generic events to legacy if not explicitly populated
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

                var evalDurationMs = Stopwatch.GetElapsedTime(evalStart).TotalMilliseconds;

                // Deduplicate people by TrackId/Detection for overlay rendering if multiple evaluators return people
                var overlayPeople = DeduplicatePeople(allPeople, tracked);

                var emittedEvents = allLegacyEvents.Select(handEvent =>
                    AddSnapshot(handEvent, overlayPeople, captured)).ToArray();

                if (_eventPublisher is not null)
                {
                    foreach (var handEvent in emittedEvents)
                    {
                        await _eventPublisher.PublishAsync(handEvent, cancellationToken);
                    }
                }

                processed++;
                var elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                var fps = elapsedSeconds > 0 ? processed / elapsedSeconds : 0;
                var beforeOverlay = Stopwatch.GetTimestamp();
                var preliminaryTotal = Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;

                var statistics = new OverlayStatistics(
                    fps,
                    preliminaryTotal,
                    _backend.Device.Name,
                    _backend.ExecutionInfo.ProviderName,
                    _modelName);

                var annotated = _overlay.Draw(captured, overlayPeople, zones, statistics);
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
                    ActiveAnalyticCount: activeEvaluatorCount);

                yield return new ProcessedCameraFrame(
                    _cameraId, annotated, overlayPeople, emittedEvents, metrics);
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
