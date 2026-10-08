using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HandRaise.Application.Inference;
using HandRaise.Application.Training;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using HandRaise.Domain.Training;
using HandRaise.Infrastructure.Windows.Inference;
using HandRaise.Infrastructure.Windows.Training;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class TrainingCoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edge-training-tests-" + Guid.NewGuid().ToString("N"));
    private TrainingPaths Paths => new(_root);
    private JsonTrainingStore Store => new(Paths);
    public TrainingCoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static TrainingProject Project(int count = 10)
    {
        var id = Guid.NewGuid();
        TrainingClass[] classes = [new(Guid.NewGuid(), "pollo"), new(Guid.NewGuid(), "caja")];
        return new(id, "Detector", "Descripción", classes,
            Enumerable.Range(0, count).Select(n => new TrainingImage(Guid.NewGuid(), id, $"image{n}.png", $"assets/{n}.png",
                8, 8, DateTimeOffset.UtcNow, TrainingSourceType.Image, "hash", null, null,
                classes.Select(c => new BoundingBoxAnnotation(c.Id, .5, .5, .4, .4)).ToArray(), true)).ToArray(),
            TrainingProjectStatus.Ready, DateTimeOffset.UtcNow);
    }
    private static TrainingJob Job(TrainingProject project) => new(Guid.NewGuid(), project.Id, TrainingJobStatus.Queued,
        TrainingProfile.Quick, TrainingResourcePolicy.PreferSystemAvailability, DateTimeOffset.UtcNow,
        TrainingProfiles.Resolve(TrainingProfile.Quick, TrainingResourcePolicy.PreferSystemAvailability), "yolo26n", project);

    [Fact]
    public async Task ProjectSupportsMultipleStableClassesAndReload()
    {
        await using var service = Service(new FakeWorker());
        var project = await service.CreateProjectAsync("Productos", "ejemplo", ["botella", "caja", "bolsa"]);
        var reloaded = await new JsonTrainingStore(Paths).LoadProjectAsync(project.Id);
        Assert.Equal(project.Classes, reloaded.Classes);
        Assert.Equal(3, reloaded.Classes.Count);
        Assert.Single(await service.ListProjectsAsync());
    }

    [Fact]
    public async Task ExistingNineteenImageProjectReloadsWithoutChangingPersistedData()
    {
        var project = Project(19);
        var projectRoot = Paths.Project(project.Id);
        foreach (var image in project.Images)
        {
            var asset = TrainingPaths.Within(projectRoot, image.StoredPath);
            Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
            await File.WriteAllBytesAsync(asset, [1, 2, 3, 4]);
        }
        await Store.SaveProjectAsync(project);
        var path = TrainingPaths.Within(projectRoot, "project.json");
        var before = await TrainingRuntimeProvisioner.HashAsync(path, default);

        var reloaded = await new JsonTrainingStore(new TrainingPaths(_root)).LoadProjectAsync(project.Id);

        Assert.Equal(19, reloaded.Images.Count);
        Assert.All(reloaded.Images, image =>
        {
            Assert.True(image.Reviewed);
            Assert.Equal(2, image.Annotations.Count);
            Assert.True(File.Exists(TrainingPaths.Within(projectRoot, image.StoredPath)));
        });
        Assert.Equal(project.Classes.Select(item => item.Id), reloaded.Classes.Select(item => item.Id));
        Assert.Equal(before, await TrainingRuntimeProvisioner.HashAsync(path, default));
    }

    [Theory]
    [InlineData(.5, .5, .4, .4, true)]
    [InlineData(.5, .5, 0, .4, false)]
    [InlineData(.9, .5, .4, .4, false)]
    [InlineData(.5, -.1, .4, .4, false)]
    [InlineData(double.NaN, .5, .4, .4, false)]
    public void BoundingBoxesValidateEdgesAndFiniteValues(double x, double y, double w, double h, bool valid)
    {
        var project = Project();
        var box = new BoundingBoxAnnotation(project.Classes[0].Id, x, y, w, h);
        if (valid) box.Validate(project.Classes); else Assert.Throws<ArgumentException>(() => box.Validate(project.Classes));
    }

    [Fact]
    public void UnknownClassAndDuplicateClassNamesAreRejected()
    {
        var project = Project();
        Assert.Throws<ArgumentException>(() => new BoundingBoxAnnotation(Guid.NewGuid(), .5, .5, .1, .1).Validate(project.Classes));
        Assert.Throws<ArgumentException>(() => (project with { Classes = [new(Guid.NewGuid(), "a"), new(Guid.NewGuid(), "A")] }).Validate());
    }

    [Fact]
    public void SplitIsStableIndependentOfInputOrderAndKeepsVideoGroupsTogether()
    {
        var project = Project(10);
        project = project with { Images = project.Images.Select((i, index) => i with { SourceId = "video-" + index / 2 }).ToArray() };
        var split = new DatasetSplitService();
        var a = split.Split(project);
        var b = split.Split(project with { Images = project.Images.Reverse().ToArray() });
        Assert.Equal(a.Train.Select(i => i.Id), b.Train.Select(i => i.Id));
        Assert.Equal(8, a.Train.Count);
        Assert.Equal(2, a.Validation.Count);
        Assert.Empty(a.Train.Select(i => i.SourceId).Intersect(a.Validation.Select(i => i.SourceId)));
        Assert.NotEmpty(a.Warnings);
    }

    [Fact]
    public void SplitRejectsUnreviewedAndSingleSourceDatasets()
    {
        var p = Project();
        Assert.Throws<ArgumentException>(() => new DatasetSplitService().Split(p with { Images = p.Images.Select(i => i with { Reviewed = false }).ToArray() }));
        Assert.Throws<ArgumentException>(() => new DatasetSplitService().Split(p with { Images = p.Images.Select(i => i with { SourceId = "same-video" }).ToArray() }));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("../../outside")]
    [InlineData("C:\\outside")]
    [InlineData("asset.png:secret")]
    public void ControlledPathsRejectEscapes(string relative) => Assert.Throws<InvalidDataException>(() => TrainingPaths.Within(_root, relative));

    [Fact]
    public async Task ImportsOwnedFramesDetectsDuplicatesAndRejectsCorruptImages()
    {
        var assets = new TrainingAssetService(Paths);
        var p = Project(0);
        var frame = new ImageFrame(8, 8, new byte[8 * 8 * 3]);
        var first = await assets.ImportFrameAsync(p, frame, TrainingSourceType.VideoFrame, "video-a", 1.25, default);
        p = p with { Images = [first] };
        var duplicate = await assets.ImportFrameAsync(p, frame, TrainingSourceType.CameraCapture, "camera-a", null, default);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.True(File.Exists(TrainingPaths.Within(Paths.Project(p.Id), first.StoredPath)));
        Assert.Equal(8, first.Width);
        Assert.Equal(1.25, first.SourceTimeSeconds);
        var corrupt = Path.Combine(_root, "broken.jpg");
        await File.WriteAllBytesAsync(corrupt, [1, 2, 3]);
        await Assert.ThrowsAsync<ArgumentException>(() => assets.ImportAsync(p, corrupt, TrainingSourceType.Image, null, null, default));
        var imported = await assets.ImportAsync(Project(0), TrainingPaths.Within(Paths.Project(p.Id), first.StoredPath), TrainingSourceType.Image, null, null, default);
        Assert.Equal(first.Sha256, imported.Sha256);
    }

    [Fact]
    public async Task ExportUsesClassIndexesInvariantCoordinatesAndSnapshot()
    {
        var project = await MaterializedProject();
        var job = Job(project);
        var exporter = new YoloDatasetExporter(Paths, new DatasetSplitService());
        var result = await exporter.ExportAsync(job, default);
        Assert.Equal(64, result.SnapshotId.Length);
        Assert.Equal(8, result.TrainImages.Count);
        var label = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(result.YamlPath)!, "labels", "train"))[0];
        Assert.Equal(new[] { "0 0.5 0.5 0.4 0.4", "1 0.5 0.5 0.4 0.4" }, await File.ReadAllLinesAsync(label));
        Assert.Contains("names:", await File.ReadAllTextAsync(result.YamlPath));
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(job, default));
    }

    [Fact]
    public async Task ExportRejectsTamperedAssetAndExternalStoredPath()
    {
        var project = await MaterializedProject();
        var exporter = new YoloDatasetExporter(Paths, new DatasetSplitService());
        await File.WriteAllBytesAsync(TrainingPaths.Within(Paths.Project(project.Id), project.Images[0].StoredPath), [9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync(Job(project), default));
        project = project with { Images = project.Images.Select(i => i with { StoredPath = "../../external.png" }).ToArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync(Job(project), default));
    }

    [Fact]
    public void StateMachineRejectsSkippingStagesAndTerminalTransitions()
    {
        var job = Job(Project());
        Assert.Throws<InvalidOperationException>(() => job.Transition(TrainingJobStatus.Completed));
        foreach (var state in new[] { TrainingJobStatus.PreparingDataset, TrainingJobStatus.Training, TrainingJobStatus.Validating,
                     TrainingJobStatus.Exporting, TrainingJobStatus.Registering, TrainingJobStatus.Completed }) job = job.Transition(state);
        Assert.True(job.IsTerminal);
        Assert.NotNull(job.StartedAtUtc);
        Assert.NotNull(job.CompletedAtUtc);
        Assert.Throws<InvalidOperationException>(() => job.Transition(TrainingJobStatus.Cancelled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JobsPersistCompletionOrIsolatedWorkerFailure(bool fail)
    {
        var project = await MaterializedProject();
        var worker = new FakeWorker { Fail = fail };
        await using var service = Service(worker);
        var queued = await service.StartAsync(project.Id, TrainingProfile.Quick, "yolo26n");
        await service.WaitAsync(queued.Id);
        var job = await Store.LoadJobAsync(queued.Id);
        Assert.Equal(fail ? TrainingJobStatus.Failed : TrainingJobStatus.Completed, job.Status);
        if (fail) { Assert.Equal("stub_crash", job.Error!.ErrorCode); Assert.Null(job.ModelId); Assert.Equal("failed", job.Progress!.ProcessState); }
        else { Assert.Equal(100, job.Progress!.Percent); Assert.Equal("completed", job.Progress.ProcessState); Assert.NotNull(job.Result); Assert.NotNull(job.ModelId); }
        Assert.Equal(project.Classes, job.Snapshot.Classes);
        Assert.NotNull(job.Dataset);
    }

    [Fact]
    public async Task CancellationPreservesProjectAndPreventsPublication()
    {
        var project = await MaterializedProject();
        var worker = new FakeWorker { Block = true };
        await using var service = Service(worker);
        var job = await service.StartAsync(project.Id, TrainingProfile.Balanced, "yolo26n");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteProjectAsync(project.Id));
        await service.CancelAsync(job.Id);
        var stored = await service.GetJobAsync(job.Id);
        Assert.Equal(TrainingJobStatus.Cancelled, stored.Status);
        Assert.Equal("cancelled", stored.Progress!.ProcessState);
        Assert.Null(stored.ModelId);
        Assert.Equal(10, (await service.GetProjectAsync(project.Id)).Images.Count);
    }

    [Fact]
    public async Task RestartMarksInterruptedJobsWithoutLaunchingWorker()
    {
        var project = await MaterializedProject();
        var job = Job(project).Transition(TrainingJobStatus.PreparingDataset);
        await Store.SaveJobAsync(job);
        var worker = new FakeWorker();
        await using var service = Service(worker);
        await service.RecoverInterruptedJobsAsync();
        Assert.False(worker.Started.Task.IsCompleted);
        Assert.Equal("edge_interrupted", (await service.GetJobAsync(job.Id)).Error!.ErrorCode);
    }

    [Fact]
    public async Task RegistryPublishesVersionsRetainsClassesReloadsAndNeverActivates()
    {
        var registry = new StandardModelRegistry(Paths.CustomModels);
        var project = await MaterializedProject();
        var publisher = new TrainingModelPublisher(Paths, registry, new FakeValidator());
        var firstJob = await PublishableJob(project);
        var first = await publisher.PublishAsync(firstJob, default);
        var second = await publisher.PublishAsync(await PublishableJob(project), default);
        Assert.Equal("1", first.Version);
        Assert.Equal("2", second.Version);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id, (await publisher.PublishAsync(firstJob, default)).Id);
        Assert.Throws<InvalidOperationException>(() => registry.RegisterModel(first));
        Assert.Empty(registry.GetProviders());
        var loaded = new StandardModelRegistry(Paths.CustomModels);
        Assert.Equal(2, loaded.ListModels().Count);
        Assert.Equal(project.Classes, loaded.GetModel(first.Id)!.CustomTraining!.Classes);
        await Store.DeleteProjectAsync(project.Id);
        Assert.True(File.Exists(first.Path));
        Assert.Equal(2, new StandardModelRegistry(Paths.CustomModels).ListModels().Count);
    }

    [Fact]
    public async Task RecoveryRecognizesPublicationCommittedBeforeCrash()
    {
        var registry = new StandardModelRegistry(Paths.CustomModels);
        var project = await MaterializedProject();
        var job = await PublishableJob(project);
        foreach (var state in new[] { TrainingJobStatus.PreparingDataset, TrainingJobStatus.Training,
                     TrainingJobStatus.Validating, TrainingJobStatus.Exporting, TrainingJobStatus.Registering }) job = job.Transition(state);
        await Store.SaveJobAsync(job);
        var publisher = new TrainingModelPublisher(Paths, registry, new FakeValidator());
        var model = await publisher.PublishAsync(job, default);
        await using var service = new TrainingApplicationService(Store, new TrainingAssetService(Paths),
            new YoloDatasetExporter(Paths, new DatasetSplitService()), new FakeWorker(), publisher, new TrainingResourceMonitor());
        await service.RecoverInterruptedJobsAsync();
        var recovered = await service.GetJobAsync(job.Id);
        Assert.Equal(TrainingJobStatus.Completed, recovered.Status);
        Assert.Equal(model.Id, recovered.ModelId);
    }

    [Fact]
    public async Task CompositionRejectsSecondOwnerAndReleasesLeaseOnDispose()
    {
        var registry = new StandardModelRegistry(Paths.CustomModels);
        var first = await TrainingCore.CreateAsync(registry, Paths);
        try { await Assert.ThrowsAsync<IOException>(() => TrainingCore.CreateAsync(registry, Paths)); }
        finally { await first.DisposeAsync(); }
        await using var second = await TrainingCore.CreateAsync(registry, Paths);
        Assert.Empty(await second.ListJobsAsync());
    }

    [Fact]
    public async Task QueuedCancellationDoesNotLaunchASecondWorker()
    {
        var firstProject = await MaterializedProject();
        var secondProject = await MaterializedProject();
        var worker = new FakeWorker { Block = true };
        await using var service = Service(worker);
        var first = await service.StartAsync(firstProject.Id, TrainingProfile.Quick, "yolo26n");
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await service.StartAsync(secondProject.Id, TrainingProfile.Quick, "yolo26n");
        await service.CancelAsync(second.Id);
        Assert.Equal(TrainingJobStatus.Cancelled, (await service.GetJobAsync(second.Id)).Status);
        Assert.Equal(TrainingJobStatus.Training, (await service.GetJobAsync(first.Id)).Status);
        await service.CancelAsync(first.Id);
    }

    [Fact]
    public async Task MissingRuntimeFailsWithoutUsingGlobalPython()
    {
        var options = TrainingRuntimeOptions.FromManagedDirectory(Paths);
        var worker = new PythonYoloTrainingWorker(Paths, options);
        var error = await Assert.ThrowsAsync<TrainingWorkerException>(() => worker.RunAsync(Job(Project()), _ => Task.CompletedTask, default));
        Assert.Equal("training_runtime_missing", error.Code);
    }

    [Fact]
    public async Task InvalidArtifactIsNeverRegisteredAndTamperingIsIsolatedOnReload()
    {
        var registry = new StandardModelRegistry(Paths.CustomModels);
        var job = await PublishableJob(await MaterializedProject());
        await Assert.ThrowsAsync<InvalidDataException>(() => new TrainingModelPublisher(Paths, registry, new FakeValidator { Fail = true }).PublishAsync(job, default));
        Assert.Empty(registry.ListModels());
        var model = await new TrainingModelPublisher(Paths, registry, new FakeValidator()).PublishAsync(job, default);
        await File.WriteAllTextAsync(model.Path, "tampered");
        var reloaded = new StandardModelRegistry(Paths.CustomModels);
        Assert.Empty(reloaded.ListModels());
        Assert.Single(reloaded.CustomModelLoadErrors);
    }

    [Fact]
    public void OnnxContractRejectsPoseDynamicAndWrongClassCount()
    {
        OnnxTrainingArtifactValidator.ValidateShape([1, 3, 640, 640], [1, 6, 8400], 640, 2, true);
        Assert.Throws<InvalidDataException>(() => OnnxTrainingArtifactValidator.ValidateShape([1, 3, 640, 640], [1, 56, 8400], 640, 2, true));
        Assert.Throws<InvalidDataException>(() => OnnxTrainingArtifactValidator.ValidateShape([-1, 3, 640, 640], [1, 6, 8400], 640, 2, true));
        Assert.Throws<InvalidDataException>(() => OnnxTrainingArtifactValidator.ValidateShape([1, 3, 640, 640], [1, 300, 6], 640, 2, true));
    }

    [Fact]
    public void ExistingPostprocessorSupportsCustomClassesWithZeroKeypoints()
    {
        var model = new ModelDescriptor("test", "Detector", "1", ModelFormat.Onnx, [InferenceCapability.ObjectDetection],
            640, 640, "test.onnx", ClassCount: 2, KeypointCount: 0);
        // One candidate, channel first: cx cy w h class0 class1.
        var result = YoloPosePostprocessor.Process([320, 320, 100, 100, .1f, .9f], [1, 6, 1], new(640, 640, 640, 640, 1, 0, 0), model);
        Assert.Single(result);
        Assert.Equal(1, result[0].ClassId);
        Assert.Empty(result[0].Keypoints);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"protocol_version\":2,\"type\":\"progress\"}")]
    [InlineData("{\"protocol_version\":1,\"type\":\"progress\",\"stage\":\"training\",\"percent\":101}")]
    [InlineData("{\"protocol_version\":1,\"type\":\"completed\"}")]
    public void ProtocolRejectsInvalidMessages(string line) => Assert.Throws<InvalidDataException>(() => TrainingWorkerProtocol.Parse(line));

    [Fact]
    public void ProtocolPreservesIndeterminateStartupActivity()
    {
        var message = TrainingWorkerProtocol.Parse("{\"protocol_version\":1,\"type\":\"progress\",\"stage\":\"training\",\"percent\":0,\"current_epoch\":0,\"total_epochs\":120,\"message\":\"Iniciando.\",\"is_indeterminate\":true}");
        Assert.True(message.IsIndeterminate);
        Assert.Equal("Iniciando.", message.Message);
    }

    [Fact]
    public async Task JobPersistsIndeterminateActivityBeforeSlowWorkerReportsEpochs()
    {
        var worker = new FakeWorker { Block = true };
        await using var service = Service(worker);
        var project = await MaterializedProject();
        var started = await service.StartAsync(project.Id, TrainingProfile.Quick, TrainingProfiles.BaseModel);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await service.GetJobAsync(started.Id);
        Assert.Equal(TrainingJobStatus.Training, running.Status);
        Assert.True(running.Progress!.IsIndeterminate);
        Assert.Equal("Iniciando entrenamiento.", running.Progress.Message);
        Assert.Equal("running", running.Progress.ProcessState);
        await service.CancelAsync(started.Id);
    }

    [Fact]
    public async Task StubProcessReportsStructuredCompletion()
    {
        var updates = new List<TrainingProgress>();
        var result = await RunStub("Complete", p => { updates.Add(p); return Task.CompletedTask; });
        Assert.Equal("stub-1", result.WorkerVersion);
        Assert.Equal(new[] { TrainingJobStatus.Training, TrainingJobStatus.Training, TrainingJobStatus.Validating, TrainingJobStatus.Exporting },
            updates.Select(p => p.Stage));
        Assert.True(updates[0].IsIndeterminate);
        Assert.Equal("Iniciando el entorno de entrenamiento.", updates[0].Message);
        Assert.Equal(25, updates[1].Percent); // epoch 1/4, independent of the worker's legacy overall percentage
        Assert.True(updates[2].IsIndeterminate);
        Assert.True(updates[3].IsIndeterminate);
        Assert.All(updates, update => Assert.False(string.IsNullOrWhiteSpace(update.LogPath)));
    }

    [Theory]
    [InlineData("Crash")]
    [InlineData("Malformed")]
    [InlineData("ChildCrash")]
    public async Task StubProcessCrashIsContainedAndDescendantsAreTerminated(string mode)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => RunStub(mode, _ => Task.CompletedTask));
        if (mode == "ChildCrash")
        {
            var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "child.pid")));
            try
            {
                using var child = Process.GetProcessById(pid);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(child.HasExited);
            }
            catch (ArgumentException) { /* Already reaped. */ }
        }
    }

    [Theory]
    [InlineData("Cancel")]
    [InlineData("IgnoreCancel")]
    public async Task StubCancellationIsGracefulOrForcedWithoutOrphan(string mode)
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunStub(mode, _ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        if (mode == "Cancel") Assert.True(File.Exists(Path.Combine(_root, "cancelled")));
    }

    private Task<TrainingResult> RunStub(string mode, Func<TrainingProgress, Task> progress, CancellationToken ct = default)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { WorkingDirectory = _root };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "TrainingWorkerStub.ps1"), "-Mode", mode }) start.ArgumentList.Add(arg);
        // Match production grace for cooperative shutdown; keep the forced-kill fixture fast.
        var grace = mode == "Cancel" ? TimeSpan.FromSeconds(8) : TimeSpan.FromMilliseconds(700);
        return new StructuredTrainingProcess().RunAsync(start, Path.Combine(_root, "worker.log"), progress, grace, TimeSpan.FromSeconds(15), ct);
    }

    private async Task<TrainingProject> MaterializedProject()
    {
        var p = Project();
        var images = new List<TrainingImage>();
        foreach (var image in p.Images)
        {
            var path = TrainingPaths.Within(Paths.Project(p.Id), image.StoredPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = [1, 2, 3, 4]; // exporter tests need no decoder or actual ONNX/training
            await File.WriteAllBytesAsync(path, bytes);
            images.Add(image with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        }
        p = p with { Images = images };
        await Store.SaveProjectAsync(p);
        return p;
    }
    private async Task<TrainingJob> PublishableJob(TrainingProject project)
    {
        var job = Job(project);
        var path = TrainingPaths.Within(Paths.Job(job.Id), "output", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        return job with { Result = new(path, "fake-1", "cpu", new Dictionary<string, double> { ["map50"] = .8 }),
            Dataset = new("snapshot", "dataset.yaml", [], [], []) };
    }
    private TrainingApplicationService Service(FakeWorker worker) => new(Store, new TrainingAssetService(Paths),
        new YoloDatasetExporter(Paths, new DatasetSplitService()), worker, new FakePublisher(), new TrainingResourceMonitor());
    private sealed class FakeValidator : ITrainingArtifactValidator
    {
        public bool Fail { get; init; }
        public Task ValidateAsync(string path, int inputSize, IReadOnlyList<TrainingClass> classes, CancellationToken ct)
        { if (Fail) throw new InvalidDataException("invalid ONNX"); return Task.CompletedTask; }
    }
    private sealed class FakePublisher : ITrainingModelPublisher
    {
        public ModelDescriptor? FindPublishedModel(Guid jobId) => null;
        public Task<ModelDescriptor> PublishAsync(TrainingJob job, CancellationToken ct) => Task.FromResult(new ModelDescriptor(
            "custom-test", "Test", "1", ModelFormat.Onnx, [InferenceCapability.ObjectDetection], 640, 640, "model.onnx", KeypointCount: 0));
    }
    private sealed class FakeWorker : ITrainingWorker
    {
        public bool Fail { get; init; }
        public bool Block { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TrainingResult> RunAsync(TrainingJob job, Func<TrainingProgress, Task> progress, CancellationToken ct)
        {
            Started.SetResult();
            if (Fail) throw new TrainingWorkerException("stub_crash", "Worker test crash.");
            if (Block) await Task.Delay(Timeout.Infinite, ct);
            await progress(new(TrainingJobStatus.Training, 25, 1, 4));
            await progress(new(TrainingJobStatus.Validating, 80));
            await progress(new(TrainingJobStatus.Exporting, 90));
            return new("model.onnx", "fake-1", "cpu", new Dictionary<string, double> { ["map50"] = .8 });
        }
    }
}
