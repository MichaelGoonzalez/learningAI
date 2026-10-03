using System.Diagnostics;
using System.Runtime.CompilerServices;
using HandRaise.Application.Capture;
using HandRaise.Application.Inference;
using HandRaise.Application.Events;
using HandRaise.Application.Overlay;
using HandRaise.Application.Tracking;
using HandRaise.Application.Storage;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;
using HandRaise.Application.Zones;

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
    private readonly string _modelName;
    private readonly IVideoFrameSink? _rawSink;
    private readonly ITracker _tracker;
    private readonly TrackedHandEvaluator _handEvaluator;
    private readonly IHandEventPublisher? _eventPublisher;
    private readonly Func<long>? _backendGeneration;
    private readonly IPersonSnapshotEncoder? _snapshotEncoder;
    private readonly SnapshotEncodingOptions? _snapshotOptions;
    private readonly Action<string>? _log;

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
        IZoneProvider? zoneProvider = null)
    {
        _cameraId = cameraId;
        _source = source;
        _backend = backend;
        _overlay = overlay;
        _detectionOptions = detectionOptions;
        _zones = zones;
        _zoneProvider = zoneProvider;
        _modelName = modelName;
        _rawSink = rawSink;
        _tracker = tracker;
        _handEvaluator = new TrackedHandEvaluator(detectionOptions);
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
                if (currentSourceGeneration != sourceGeneration || currentBackendGeneration != backendGeneration)
                {
                    _tracker.Reset();
                    _handEvaluator.Reset();
                    sourceGeneration = currentSourceGeneration;
                    backendGeneration = currentBackendGeneration;
                }

                if (_rawSink is not null)
                {
                    await _rawSink.WriteAsync(captured, cancellationToken);
                }

                var frameStarted = Stopwatch.GetTimestamp();
                var pose = await _backend.InferAsync(captured.AsImageFrame(), cancellationToken);
                var tracked = _tracker.Update(pose.People, captured.TimestampMilliseconds);
                var zones = _zoneProvider?.GetZones(captured.Width, captured.Height) ?? _zones;
                var evaluation = _handEvaluator.Evaluate(
                    _cameraId,
                    tracked,
                    captured.TimestampMilliseconds,
                    captured.TimestampUtc,
                    zones);
                var emittedEvents = evaluation.Events.Select(handEvent =>
                    AddSnapshot(handEvent, evaluation.People, captured)).ToArray();
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
                var annotated = _overlay.Draw(captured, evaluation.People, zones, statistics);
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
                    totalTime.TotalMilliseconds);
                yield return new ProcessedCameraFrame(
                    _cameraId, annotated, evaluation.People, emittedEvents, metrics);
            }
        }
    }

    private HandEvent AddSnapshot(
        HandEvent handEvent,
        IReadOnlyList<EvaluatedPerson> people,
        VideoFrame frame)
    {
        if (handEvent.Type != "hand_raised" || _snapshotEncoder is null || _snapshotOptions is null)
        {
            return handEvent;
        }

        var person = people.FirstOrDefault(item => item.TrackId == handEvent.TrackId);
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
