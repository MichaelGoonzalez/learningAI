using System.Diagnostics;
using System.Runtime.CompilerServices;
using HandRaise.Application.Analytics;
using HandRaise.Application.Capture;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Application.Overlay;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Tracking;
using HandRaise.Application.Training;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Models;
using HandRaise.Domain.Training;
using HandRaise.Domain.Zones;
using HandRaise.Infrastructure.Windows.Inference;
using HandRaise.Infrastructure.Windows.Storage;
using HandRaise.Infrastructure.Windows.Training;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class CustomObjectLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edge-custom-live-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private async Task<(StandardModelRegistry Registry, ModelDescriptor Model)> PublishAsync()
    {
        var paths = new TrainingPaths(_root);
        var project = new TrainingProject(Guid.NewGuid(), "Aves", "", [new(Guid.NewGuid(), "Pollo"), new(Guid.NewGuid(), "Gallo")], [], TrainingProjectStatus.Draft, DateTimeOffset.UtcNow);
        var jobId = Guid.NewGuid();
        var file = TrainingPaths.Within(paths.Job(jobId), "output", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [1, 2, 3]); // fake artifact; transport/publication contracts, not ONNX validation
        var job = new TrainingJob(jobId, project.Id, TrainingJobStatus.Registering, TrainingProfile.Quick, TrainingResourcePolicy.PreferSystemAvailability,
            DateTimeOffset.UtcNow, TrainingProfiles.Resolve(TrainingProfile.Quick, TrainingResourcePolicy.PreferSystemAvailability), "base", project,
            Dataset: new("snapshot", "dataset", [], [], []), Result: new(file, "fake", "cpu", new Dictionary<string, double>()));
        var registry = new StandardModelRegistry(paths.CustomModels);
        var model = await new TrainingModelPublisher(paths, registry, new Validator()).PublishAsync(job, default);
        return (new StandardModelRegistry(paths.CustomModels), model);
    }
    private static CameraAnalyticInstance Instance(string id, string camera, string model) => new(id, camera, CustomObjectEvaluator.TypeId, "Aves",
        Configuration: new Dictionary<string, object?> { ["model_id"] = model, ["confidence_threshold"] = .5 });
    private static DetectionOptions Options() => new() { KeypointConfidence = .5, StrictMode = false, StrictMarginPixels = 0,
        ConsecutiveFrames = 1, LowerConsecutiveFrames = 1, Cooldown = TimeSpan.Zero, TrackTimeToLive = TimeSpan.FromSeconds(1) };

    [Fact]
    public async Task PublishedModelReloadedCameraSingleCaptureProducesRealStructuredClassResults()
    {
        var (registry, model) = await PublishAsync();
        var storePath = Path.Combine(_root, "analytics.json");
        using (var store = new JsonAnalyticInstanceStore(storePath))
        {
            await store.SaveAsync(Instance("a", "camera", model.Id));
            await store.SaveAsync(Instance("b", "camera", model.Id));
        }
        using var reload = new JsonAnalyticInstanceStore(storePath);
        var instances = await reload.GetByCameraIdAsync("camera");
        var factory = new AnalyticEvaluatorFactory();
        var source = new Source(); var objects = new Backend(); var pose = new Backend(); var tracker = new Tracker();
        var loads = 0;
        await using var runtime = new CustomObjectRuntime(registry, (descriptor, ct) => { loads++; Assert.Equal(model.Id, descriptor.Id); return Task.FromResult<IInferenceBackend>(objects); });
        var pipeline = new CameraPipeline("camera", source, pose, new Overlay(), Options(), [], "builtin", tracker,
            evaluators: instances.Select(i => factory.CreateEvaluator(i, Options())).ToArray(), customRuntime: runtime);
        var frames = 0;
        await foreach (var frame in pipeline.RunAsync())
        {
            using (frame)
            {
                frames++;
                var d = Assert.Single(frame.Objects);
                Assert.Equal("Gallo", d.ClassName);
                Assert.Equal(model.CustomTraining!.Classes[1].Id, d.ClassId);
                Assert.Equal(1, d.ClassIndex);
                Assert.Empty(frame.People);
                Assert.All(frame.Events, e => { Assert.Equal("custom_object_detected", e.Type); Assert.Equal("Gallo", e.Metadata!["class_name"]); });
            }
        }
        Assert.Equal(3, frames); Assert.Equal(1, source.Reads); Assert.Equal(1, loads);
        Assert.Equal(3, objects.Calls); Assert.Equal(0, pose.Calls); Assert.Equal(0, tracker.DetectionCount);
        var plan = new CapabilityPlanner(registry).CreatePlan("camera", instances, new StandardAnalyticCatalog());
        Assert.Equal(model.Id, Assert.Single(plan.SelectedProviders).ModelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrIncompatibleModelIsIsolatedAndDoesNotRetryEveryFrame(bool incompatible)
    {
        var (registry, model) = await PublishAsync(); var calls = 0;
        await using var broken = new CustomObjectRuntime(registry, (m, ct) => { calls++; throw new InvalidDataException("incompatible"); });
        var evaluator = new CustomObjectEvaluator(Instance("a", "one", incompatible ? model.Id : "missing"));
        for (var i = 0; i < 3; i++)
        {
            var result = await broken.ProcessAsync("one", new(2, 2, new byte[12]), DateTimeOffset.UtcNow, [evaluator], default);
            Assert.Single(result.Errors); Assert.Empty(result.Detections); Assert.Empty(result.Events);
        }
        Assert.Equal(incompatible ? 1 : 0, calls);
        await using var healthy = new CustomObjectRuntime(registry, (m, ct) => Task.FromResult<IInferenceBackend>(new Backend()));
        var valid = await healthy.ProcessAsync("two", new(2, 2, new byte[12]), DateTimeOffset.UtcNow,
            [new(Instance("b", "two", model.Id))], default);
        Assert.Single(valid.Detections); Assert.Empty(valid.Errors);
        Assert.Equal("two", Assert.Single(valid.Events).CameraId);
    }

    [Fact]
    public async Task DisablingCustomSolutionReleasesBackendAndEmitsNoEvents()
    {
        var (registry, model) = await PublishAsync(); var backend = new Backend();
        await using var runtime = new CustomObjectRuntime(registry, (m, ct) => Task.FromResult<IInferenceBackend>(backend));
        await runtime.ProcessAsync("c", new(2, 2, new byte[12]), DateTimeOffset.UtcNow, [new(Instance("a", "c", model.Id))], default);
        var result = await runtime.ProcessAsync("c", new(2, 2, new byte[12]), DateTimeOffset.UtcNow, [], default);
        Assert.True(backend.Disposed); Assert.Empty(result.Events); Assert.Empty(result.Detections);
    }

    [Fact]
    public async Task BrokenCustomModelDoesNotInterruptBuiltinEvaluationInSameCamera()
    {
        var (registry, model) = await PublishAsync();
        await using var runtime = new CustomObjectRuntime(registry, (m, ct) => throw new InvalidDataException("incompatible"));
        var builtin = new Counter(); var backend = new Backend(); var tracker = new Tracker(); var errors = 0;
        var pipeline = new CameraPipeline("camera", new Source(), backend, new Overlay(), Options(), [], "builtin", tracker,
            evaluators: [builtin, new CustomObjectEvaluator(Instance("a", "camera", model.Id))], customRuntime: runtime,
            customErrors: e => errors += e.Count);
        await foreach (var frame in pipeline.RunAsync()) using (frame) { Assert.Empty(frame.Objects); }
        Assert.Equal(3, errors); Assert.Equal(3, builtin.Calls); Assert.Equal(3, backend.Calls); Assert.Equal(3, tracker.DetectionCount);
    }

    [Fact]
    public void ConfidenceIsIndependentOfDesktopCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("es-CO");
            var instance = Instance("a", "camera", "model") with { Configuration = new Dictionary<string, object?> { ["confidence_threshold"] = .85 } };
            Assert.Equal(.85, new CustomObjectEvaluator(instance).Confidence);
            Assert.True(CustomObjectEvaluator.IsCustom("CUSTOM_OBJECT_DETECTION"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task InvalidRealArtifactFailsBeforeInference()
    {
        var (registry, model) = await PublishAsync();
        await using var runtime = new CustomObjectRuntime(registry);
        var result = await runtime.ProcessAsync("c", new(2, 2, new byte[12]), DateTimeOffset.UtcNow, [new(Instance("a", "c", model.Id))], default);
        Assert.Single(result.Errors); Assert.Empty(result.Detections);
    }

    [Fact]
    public void DetectPostprocessorUsesClassAwareNmsAndLetterboxCoordinates()
    {
        var model = new ModelDescriptor("model", "Detect", "1", ModelFormat.Onnx, [InferenceCapability.ObjectDetection], 640, 640, "fake", ClassCount: 2, KeypointCount: 0);
        var tensor = new float[6 * 7];
        for (var i = 0; i < 3; i++) { tensor[i] = 320; tensor[7 + i] = 320; tensor[14 + i] = 200; tensor[21 + i] = 100; }
        tensor[28] = .9f; tensor[29] = .8f; tensor[35 + 2] = .95f;
        var result = ObjectDetectionPostprocessor.Process(tensor, [1, 6, 7], new(320, 160, 640, 640, 2, 0, 160), model);
        Assert.Equal(2, result.Count); Assert.Equal(1, result[0].ClassId);
        Assert.Equal(new BoundingBox(110, 55, 210, 105), result[0].Box);
        Assert.All(result, d => Assert.Empty(d.Keypoints));
        tensor[35] = float.NaN;
        Assert.Throws<ArgumentException>(() => ObjectDetectionPostprocessor.Process(tensor, [1, 6, 7], new(320, 160, 640, 640, 2, 0, 160), model));
        Assert.Throws<ArgumentException>(() => ObjectDetectionPostprocessor.Process(new float[42], [1, 7, 6], default, model));
    }

    [Fact]
    public async Task DraftBoxesRemainUnreviewedAndExplicitNegativePersists()
    {
        var paths = new TrainingPaths(_root); var registry = new StandardModelRegistry(paths.CustomModels);
        await using var core = await TrainingCore.CreateAsync(registry, paths);
        var p = await core.CreateProjectAsync("Aves", "", ["Ave"]);
        p = await core.ImportFrameAsync(p.Id, new(8, 8, new byte[192]), TrainingSourceType.VideoFrame, "video");
        p = await core.SetAnnotationsAsync(p.Id, p.Images[0].Id, [new(p.Classes[0].Id, .5, .5, .25, .25)], false);
        Assert.False(p.Images[0].Reviewed);
        Assert.Throws<ArgumentException>(() => new DatasetSplitService().Split(p));
        await core.SetAnnotationsAsync(p.Id, p.Images[0].Id, []);
        var reloaded = await new JsonTrainingStore(paths).LoadProjectAsync(p.Id);
        Assert.True(reloaded.Images[0].Reviewed); Assert.Empty(reloaded.Images[0].Annotations); Assert.NotNull(reloaded.UpdatedAtUtc);
    }

    private sealed class Validator : ITrainingArtifactValidator
    { public Task ValidateAsync(string path, int inputSize, IReadOnlyList<TrainingClass> classes, CancellationToken ct) => Task.CompletedTask; }
    private sealed class Counter : IAnalyticEvaluator
    {
        public int Calls;
        public string AnalyticTypeId => "person_presence";
        public string InstanceId => "builtin";
        public AnalyticEvaluationResult Evaluate(AnalyticFrameContext context) { Calls++; return AnalyticEvaluationResult.Empty; }
        public void Reset() { }
    }
    private sealed class Backend : IInferenceBackend
    {
        public int Calls; public bool Disposed;
        public DeviceInfo Device => new("cpu", "CPU", HardwareVendor.Cpu, InferenceBackend.Cpu, null, null, null, true);
        public InferenceExecutionInfo ExecutionInfo => new("CPU", "cpu", "CPU", false, "fake");
        public ValueTask LoadAsync(ModelDescriptor model, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<PoseBatch> InferAsync(ImageFrame frame, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(new PoseBatch([new(new(0, 0, 1, 1), .91f, 1, [])], TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Tracker : ITracker
    {
        public int DetectionCount;
        public TrackerUpdate Update(IReadOnlyList<PosePerson> detections, double timestampMilliseconds) { DetectionCount += detections.Count; return new([], new HashSet<int>()); }
        public void Reset() { }
    }
    private sealed class Overlay : IFrameOverlay
    { public VideoFrame Draw(VideoFrame source, IReadOnlyList<EvaluatedPerson> people, IReadOnlyList<ZoneDefinition> zones, OverlayStatistics statistics) => source.Clone(); }
    private sealed class Source : IVideoSource
    {
        public int Reads;
        public VideoSourceKind Kind => VideoSourceKind.File;
        public string DisplayName => "fake";
        public double FramesPerSecond => 30;
        public long DroppedFrames => 0;
        public async IAsyncEnumerable<VideoFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Reads++; for (var n = 0; n < 3; n++) { yield return new(2, 2, new byte[12], n, n * 1000, Stopwatch.GetTimestamp(), DateTimeOffset.UnixEpoch.AddSeconds(n)); await Task.Yield(); } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
