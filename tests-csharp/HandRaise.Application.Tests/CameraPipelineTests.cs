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

public sealed class CameraPipelineTests
{
    [Fact]
    public async Task TwoPipelines_AreIndependent()
    {
        await using var backend = new FakeBackend();
        var first = CreatePipeline("camera-a", new FakeSource(2), backend);
        var second = CreatePipeline("camera-b", new FakeSource(3), backend);

        var results = await Task.WhenAll(Collect(first), Collect(second));

        Assert.Equal(2, results[0].Count);
        Assert.All(results[0], id => Assert.Equal("camera-a", id));
        Assert.Equal(3, results[1].Count);
        Assert.All(results[1], id => Assert.Equal("camera-b", id));
    }

    [Fact]
    public async Task Pipeline_DisposesCapturedBuffersAfterUse()
    {
        var disposed = 0;
        await using var backend = new FakeBackend();
        var pipeline = CreatePipeline("camera", new FakeSource(1, () => disposed++), backend);

        await foreach (var result in pipeline.RunAsync())
        {
            result.Dispose();
        }

        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task Pipeline_ResetsTrackerAfterSourceReconnect()
    {
        await using var backend = new FakeBackend();
        var tracker = new CountingTracker();
        var pipeline = CreatePipeline("camera", new ReconnectingSource(), backend, tracker);

        await Collect(pipeline);

        Assert.Equal(2, tracker.ResetCount);
    }

    [Fact]
    public async Task Pipeline_ResetsTrackerAfterBackendSwitch()
    {
        await using var backend = new FakeBackend();
        var tracker = new CountingTracker();
        long generation = 0;
        var pipeline = CreatePipeline(
            "camera", new CallbackSource(() => generation++), backend, tracker, () => generation);

        await Collect(pipeline);

        Assert.Equal(1, tracker.ResetCount);
    }

    private static CameraPipeline CreatePipeline(
        string id,
        IVideoSource source,
        IInferenceBackend backend,
        ITracker? tracker = null,
        Func<long>? backendGeneration = null) => new(
            id,
            source,
            backend,
            new CloneOverlay(),
            new DetectionOptions
            {
                KeypointConfidence = 0.5,
                ShoulderMarginRatio = 0.15,
                StrictMode = false,
                StrictMarginPixels = 0,
                ConsecutiveFrames = 1,
                LowerConsecutiveFrames = 1,
                Cooldown = TimeSpan.Zero,
                TrackTimeToLive = TimeSpan.FromSeconds(1)
            },
            [],
            "model",
            tracker ?? new ByteTracker(new TrackerOptions
            {
                HighConfidenceThreshold = 0.5f,
                LowConfidenceThreshold = 0.1f,
                NewTrackThreshold = 0.5f,
                FirstMatchIouThreshold = 0.3f,
                SecondMatchIouThreshold = 0.2f,
                LostTrackBufferFrames = 3,
                NominalFramesPerSecond = 30,
                PositionProcessNoise = 1,
                VelocityProcessNoise = 0.1,
                MeasurementNoise = 10
            }),
            backendGeneration: backendGeneration);

    private static async Task<List<string>> Collect(CameraPipeline pipeline)
    {
        var cameraIds = new List<string>();
        await foreach (var result in pipeline.RunAsync())
        {
            using (result)
            {
                cameraIds.Add(result.CameraId);
            }
        }

        return cameraIds;
    }

    private sealed class FakeSource(int frames, Action? onDispose = null) : IVideoSource
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
                yield return new VideoFrame(
                    1, 1, [0, 0, 0], index, index * 33, Stopwatch.GetTimestamp(),
                    DateTimeOffset.UnixEpoch.AddMilliseconds(index * 33), onDispose);
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
                yield return new VideoFrame(
                    1, 1, [0, 0, 0], index, index * 33, Stopwatch.GetTimestamp(),
                    DateTimeOffset.UnixEpoch.AddMilliseconds(index * 33));
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CallbackSource(Action afterFirstFrame) : IVideoSource
    {
        public VideoSourceKind Kind => VideoSourceKind.File;
        public string DisplayName => "callback";
        public double FramesPerSecond => 30;
        public long DroppedFrames => 0;

        public async IAsyncEnumerable<VideoFrame> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new VideoFrame(
                1, 1, [0, 0, 0], 0, 0, Stopwatch.GetTimestamp(), DateTimeOffset.UnixEpoch);
            await Task.Yield();
            afterFirstFrame();
            yield return new VideoFrame(
                1, 1, [0, 0, 0], 1, 33, Stopwatch.GetTimestamp(),
                DateTimeOffset.UnixEpoch.AddMilliseconds(33));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class CloneOverlay : IFrameOverlay
    {
        public VideoFrame Draw(
            VideoFrame source,
            IReadOnlyList<EvaluatedPerson> people,
            IReadOnlyList<ZoneDefinition> zones,
            OverlayStatistics statistics) => source.Clone();
    }
}
