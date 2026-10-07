using System.Diagnostics;
using System.Runtime.CompilerServices;
using HandRaise.Application.Analytics;
using HandRaise.Application.Capture;
using HandRaise.Application.Events;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Application.Overlay;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Tests;

public sealed class AnalyticsAbstractionTests
{
    private static DetectionOptions CreateOptions() => new()
    {
        KeypointConfidence = 0.5,
        ShoulderMarginRatio = 0.15,
        StrictMode = false,
        StrictMarginPixels = 0,
        ConsecutiveFrames = 1,
        LowerConsecutiveFrames = 1,
        Cooldown = TimeSpan.Zero,
        TrackTimeToLive = TimeSpan.FromSeconds(1)
    };

    private static VideoFrame CreateFrame(int index) =>
        new(640, 480, new byte[640 * 480 * 3], index, index * 33, Stopwatch.GetTimestamp(),
            DateTimeOffset.UnixEpoch.AddMilliseconds(index * 33));

    [Fact]
    public void TrackedHandEvaluator_Implements_IAnalyticEvaluator()
    {
        var evaluator = new TrackedHandEvaluator(CreateOptions());
        Assert.IsAssignableFrom<IAnalyticEvaluator>(evaluator);
        Assert.Equal("hand_raise", evaluator.AnalyticTypeId);
    }

    [Fact]
    public void HandRaiseAnalyticEvaluator_ProducesGenericAndLegacyEvents()
    {
        var evaluator = new HandRaiseAnalyticEvaluator(CreateOptions(), "hand_eval_1");
        var keypoints = new Keypoint[17];
        for (var i = 0; i < 17; i++) keypoints[i] = new Keypoint(100, 100, 0.9);
        // Raise right wrist above right shoulder
        keypoints[6] = new Keypoint(100, 200, 0.9); // Right shoulder
        keypoints[10] = new Keypoint(100, 50, 0.9); // Right wrist

        var person = new PosePerson(new BoundingBox(50, 50, 150, 250), 0.9f, 0, keypoints);
        var tracked = new TrackedPose(person, 1);
        var update = new TrackerUpdate([tracked], new HashSet<int> { 1 });

        var context = new AnalyticFrameContext(
            "cam-1",
            33.0,
            DateTimeOffset.UtcNow,
            640,
            480,
            update,
            []);

        var result = evaluator.Evaluate(context);

        Assert.NotEmpty(result.Events);
        Assert.NotEmpty(result.LegacyEvents);
        Assert.Equal("hand_raised", result.Events[0].EventType);
        Assert.Equal("hand_raise", result.Events[0].AnalyticType);
        Assert.Equal("hand_eval_1", result.Events[0].AnalyticInstanceId);
        Assert.Equal("hand_raised", result.LegacyEvents[0].Type);
    }

    [Fact]
    public void AnalyticEventAdapter_ConvertsBiDirectionally()
    {
        var generic = new AnalyticEvent(
            Id: "ev-1",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-1",
            AnalyticType: "hand_raise",
            EventType: "hand_raised",
            TimestampUtc: DateTimeOffset.UtcNow,
            TrackId: 7,
            ZoneId: "zone-a",
            Confidence: 0.95,
            Metadata: new Dictionary<string, object?> { ["hand"] = "right" });

        var legacy = AnalyticEventAdapter.ToLegacyHandEvent(generic);
        Assert.Equal(generic.Id, legacy.Id);
        Assert.Equal("right", legacy.Hand);
        Assert.Equal("zone-a", legacy.Zone);
        Assert.Equal(7, legacy.TrackId);

        var convertedBack = AnalyticEventAdapter.FromLegacyHandEvent(legacy, "inst-1");
        Assert.Equal(generic.Id, convertedBack.Id);
        Assert.Equal("right", convertedBack.Metadata?["hand"]);
    }

    [Fact]
    public async Task CameraPipeline_WithZeroEvaluators_ExecutesWithoutError()
    {
        await using var backend = new FakeBackend();
        var pipeline = new CameraPipeline(
            "camera-0",
            new FakeSource(2),
            backend,
            new FakeOverlay(),
            CreateOptions(),
            [],
            "test-model",
            new CountingTracker(),
            evaluators: []);

        var count = 0;
        await foreach (var frame in pipeline.RunAsync())
        {
            using (frame)
            {
                count++;
                Assert.Empty(frame.Events);
            }
        }

        Assert.Equal(2, count);
        Assert.Equal(0, backend.InferenceCount);
    }

    [Fact]
    public async Task CameraPipeline_WithMultipleEvaluators_ExecutesSingleInferencePerFrame()
    {
        await using var backend = new FakeBackend();
        var eval1 = new GenericMockEvaluator("eval_1", "type_a");
        var eval2 = new GenericMockEvaluator("eval_2", "type_b");
        var eval3 = new GenericMockEvaluator("eval_3", "type_c");

        var pipeline = new CameraPipeline(
            "camera-multi",
            new FakeSource(3),
            backend,
            new FakeOverlay(),
            CreateOptions(),
            [],
            "test-model",
            new CountingTracker(),
            evaluators: [eval1, eval2, eval3]);

        var count = 0;
        await foreach (var frame in pipeline.RunAsync())
        {
            using (frame)
            {
                count++;
                Assert.Equal(3, frame.Metrics.ActiveAnalyticCount);
            }
        }

        Assert.Equal(3, count);
        // Inference is executed exactly once per frame (3 total)
        Assert.Equal(3, backend.InferenceCount);
        // Each evaluator receives all 3 frames
        Assert.Equal(3, eval1.EvaluationCount);
        Assert.Equal(3, eval2.EvaluationCount);
        Assert.Equal(3, eval3.EvaluationCount);
    }

    [Fact]
    public async Task CameraPipeline_EvaluatorException_DoesNotCrashPipelineOrOtherEvaluators()
    {
        await using var backend = new FakeBackend();
        var goodEval1 = new GenericMockEvaluator("good_1", "type_good");
        var failingEval = new FailingMockEvaluator("bad_1", "type_bad");
        var goodEval2 = new GenericMockEvaluator("good_2", "type_good");

        var logMessages = new List<string>();
        var pipeline = new CameraPipeline(
            "camera-fault",
            new FakeSource(2),
            backend,
            new FakeOverlay(),
            CreateOptions(),
            [],
            "test-model",
            new CountingTracker(),
            log: logMessages.Add,
            evaluators: [goodEval1, failingEval, goodEval2]);

        var count = 0;
        await foreach (var frame in pipeline.RunAsync())
        {
            using (frame)
            {
                count++;
                Assert.True(frame.Metrics.EvaluatorErrors > 0);
            }
        }

        Assert.Equal(2, count);
        Assert.Equal(2, goodEval1.EvaluationCount);
        Assert.Equal(2, goodEval2.EvaluationCount);
        Assert.NotEmpty(logMessages);
    }

    [Fact]
    public async Task CameraPipeline_ResetsAllMultipleEvaluators_OnSourceReconnect()
    {
        await using var backend = new FakeBackend();
        var eval1 = new GenericMockEvaluator("eval_1", "type_a");
        var eval2 = new GenericMockEvaluator("eval_2", "type_b");

        var pipeline = new CameraPipeline(
            "camera-reset",
            new ReconnectingSource(),
            backend,
            new FakeOverlay(),
            CreateOptions(),
            [],
            "test-model",
            new CountingTracker(),
            evaluators: [eval1, eval2]);

        await foreach (var frame in pipeline.RunAsync())
        {
            frame.Dispose();
        }

        Assert.Equal(2, eval1.ResetCount);
        Assert.Equal(2, eval2.ResetCount);
    }

    private sealed class GenericMockEvaluator(string instanceId, string analyticTypeId) : IAnalyticEvaluator
    {
        public string InstanceId { get; } = instanceId;
        public string AnalyticTypeId { get; } = analyticTypeId;
        public bool IsEnabled => true;
        public int EvaluationCount { get; private set; }
        public int ResetCount { get; private set; }

        public AnalyticEvaluationResult Evaluate(AnalyticFrameContext context)
        {
            EvaluationCount++;
            return AnalyticEvaluationResult.Empty;
        }

        public void Reset() => ResetCount++;
    }

    private sealed class FailingMockEvaluator(string instanceId, string analyticTypeId) : IAnalyticEvaluator
    {
        public string InstanceId { get; } = instanceId;
        public string AnalyticTypeId { get; } = analyticTypeId;
        public bool IsEnabled => true;

        public AnalyticEvaluationResult Evaluate(AnalyticFrameContext context)
        {
            throw new InvalidOperationException("Simulation of evaluator failure.");
        }

        public void Reset() { }
    }

    private sealed class CountingTracker : ITracker
    {
        public int ResetCount { get; private set; }

        public TrackerUpdate Update(
            IReadOnlyList<PosePerson> detections,
            double timestampMilliseconds) => new([], new HashSet<int>());

        public void Reset() => ResetCount++;
    }

    private sealed class FakeBackend : IInferenceBackend
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public int InferenceCount { get; private set; }
        public DeviceInfo Device { get; } = new(
            "cpu", "CPU", HardwareVendor.Cpu, InferenceBackend.Cpu, null, null, null, true);
        public InferenceExecutionInfo ExecutionInfo { get; } = new(
            "CPUExecutionProvider", "cpu", "CPU", false, "fake");

        public ValueTask LoadAsync(ModelDescriptor model, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async ValueTask<PoseBatch> InferAsync(
            ImageFrame frame,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                InferenceCount++;
                await Task.Yield();
                return new PoseBatch([], TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
            }
            finally
            {
                _gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeOverlay : IFrameOverlay
    {
        public VideoFrame Draw(
            VideoFrame source,
            IReadOnlyList<EvaluatedPerson> people,
            IReadOnlyList<ZoneDefinition> zones,
            OverlayStatistics statistics) => source.Clone();
    }

    private sealed class FakeSource(int frames) : IVideoSource
    {
        public VideoSourceKind Kind => VideoSourceKind.File;
        public string DisplayName => "fake";
        public double FramesPerSecond => 30;
        public long DroppedFrames => 0;

        public async IAsyncEnumerable<VideoFrame> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < frames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CreateFrame(index);
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReconnectingSource : IVideoSource, IConnectionGenerationSource
    {
        public VideoSourceKind Kind => VideoSourceKind.Webcam;
        public string DisplayName => "reconnecting";
        public double FramesPerSecond => 30;
        public long DroppedFrames => 0;
        public long ConnectionGeneration { get; private set; }

        public async IAsyncEnumerable<VideoFrame> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < 2; index++)
            {
                ConnectionGeneration++;
                yield return CreateFrame(index);
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
