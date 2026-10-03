using System.Diagnostics;
using System.Runtime.CompilerServices;
using HandRaise.Application.Capture;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Application.Overlay;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Tests;

public sealed class CameraPipelineWatchdogTests
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

    [Fact]
    public async Task Watchdog_DetectsConnectionGenerationChange_AndResetsState()
    {
        await using var backend = new FakeBackend();
        var tracker = new CountingTracker();
        var pipeline = new CameraPipeline(
            "camera-watchdog",
            new ReconnectingSource(),
            backend,
            new FakeOverlay(),
            CreateOptions(),
            [],
            "test-model",
            tracker);

        var count = 0;
        await foreach (var frame in pipeline.RunAsync())
        {
            using (frame) count++;
        }

        Assert.Equal(3, count);
        Assert.Equal(2, tracker.ResetCount);
    }

    [Fact]
    public async Task Watchdog_IsolatesPipelineFailureFromOtherPipelines()
    {
        await using var backend = new FakeBackend();
        var healthyPipeline = new CameraPipeline(
            "camera-healthy",
            new FakeSource(3),
            backend,
            new FakeOverlay(),
            CreateOptions(),
            [],
            "test-model",
            new CountingTracker());

        var failingPipeline = new CameraPipeline(
            "camera-failing",
            new ThrowingSource(),
            backend,
            new FakeOverlay(),
            CreateOptions(),
            [],
            "test-model",
            new CountingTracker());

        var healthyTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var frame in healthyPipeline.RunAsync())
            {
                using (frame) count++;
            }
            return count;
        });

        var failingTask = Task.Run(async () =>
        {
            await foreach (var frame in failingPipeline.RunAsync())
            {
                using (frame) { }
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => failingTask);
        var healthyCount = await healthyTask;
        Assert.Equal(3, healthyCount);
    }

    [Fact]
    public async Task Watchdog_RecoversStateAfterReconnection()
    {
        await using var backend = new FakeBackend();
        var tracker = new CountingTracker();
        var source = new MultiPhaseReconnectingSource();
        var pipeline = new CameraPipeline(
            "camera-recovering",
            source,
            backend,
            new FakeOverlay(),
            CreateOptions(),
            [],
            "test-model",
            tracker);

        var processed = 0;
        await foreach (var frame in pipeline.RunAsync())
        {
            using (frame) processed++;
        }

        Assert.Equal(4, processed);
        Assert.True(tracker.ResetCount >= 1);
    }

    [Fact]
    public async Task Watchdog_HandlesEmptyStreamGracefully()
    {
        await using var backend = new FakeBackend();
        var pipeline = new CameraPipeline(
            "camera-empty",
            new FakeSource(0),
            backend,
            new FakeOverlay(),
            CreateOptions(),
            [],
            "test-model",
            new CountingTracker());

        var count = 0;
        await foreach (var frame in pipeline.RunAsync())
        {
            using (frame) count++;
        }

        Assert.Equal(0, count);
    }

    private static VideoFrame CreateFrame(int index) =>
        new(640, 480, new byte[640 * 480 * 3], index, index * 33, Stopwatch.GetTimestamp(),
            DateTimeOffset.UnixEpoch.AddMilliseconds(index * 33));

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
            ConnectionGeneration++;
            yield return CreateFrame(0);
            await Task.Yield();
            ConnectionGeneration++;
            yield return CreateFrame(1);
            await Task.Yield();
            yield return CreateFrame(2);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingSource : IVideoSource
    {
        public VideoSourceKind Kind => VideoSourceKind.Webcam;
        public string DisplayName => "throwing";
        public double FramesPerSecond => 30.0;
        public long DroppedFrames => 0;

        public async IAsyncEnumerable<VideoFrame> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Fallo inducido en la captura");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MultiPhaseReconnectingSource : IVideoSource, IConnectionGenerationSource
    {
        public VideoSourceKind Kind => VideoSourceKind.Webcam;
        public string DisplayName => "multiphase";
        public double FramesPerSecond => 30.0;
        public long DroppedFrames => 0;
        public long ConnectionGeneration { get; private set; }

        public async IAsyncEnumerable<VideoFrame> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return CreateFrame(0);
            yield return CreateFrame(1);
            ConnectionGeneration++;
            await Task.Yield();
            yield return CreateFrame(2);
            yield return CreateFrame(3);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
