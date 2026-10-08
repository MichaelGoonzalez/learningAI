using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HandRaise.Application.Training;
using HandRaise.Domain.Training;
using HandRaise.Domain.Models;
using HandRaise.Infrastructure.Windows.Training;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class TrainingRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edge-runtime-tests-" + Guid.NewGuid().ToString("N"));
    private TrainingPaths Paths => new(_root);
    private string Assets => Path.Combine(_root, "assets");
    public TrainingRuntimeTests()
    {
        Directory.CreateDirectory(Assets);
        File.WriteAllText(Path.Combine(Assets, "worker.py"), "# fixture worker, never executed");
        File.WriteAllText(Path.Combine(Assets, "requirements.txt"), "fixture==1.0.0");
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private TrainingRuntimeProvisioner Provisioner(Source source, Probe? probe = null)
    {
        probe ??= new Probe();
        return new(Paths, source, probe, Assets, probe);
    }
    private Source NewSource() => new(Assets);

    [Fact]
    public async Task MissingRuntimeDoesNotUseGlobalPythonOrNetwork()
    {
        var source = NewSource(); var probe = new Probe(); var manager = Provisioner(source, probe);
        Assert.Equal(TrainingRuntimeState.NotInstalled, (await manager.CheckAsync()).State);
        Assert.Equal(0, source.Resolutions); Assert.Equal(0, probe.Calls);
        Assert.StartsWith(manager.Current, TrainingRuntimeOptions.FromManagedDirectory(Paths).PythonExecutable);
    }
    [Fact]
    public async Task PrepareVerifiesCommitsAndDetectsTampering()
    {
        var manager = Provisioner(NewSource());
        var progress = new Reports();
        Assert.Equal(TrainingRuntimeState.Ready, (await manager.PrepareAsync(progress)).State);
        Assert.Equal(100, progress.Items.Last().Percent);
        Assert.Contains(progress.Items, p => p.Message.Contains("Comprobando componentes"));
        Assert.Contains(progress.Items, p => p.Message.Contains("Descargando runtime privado"));
        Assert.Contains(progress.Items, p => p.Message.Contains("Verificando"));
        Assert.Contains(progress.Items, p => p.Message.Contains("Preparando modelo base"));
        Assert.Contains(progress.Items, p => p.Message.Contains("Validando el entorno"));
        Assert.Equal(TrainingRuntimeState.Ready, progress.Items.Last().State);
        Assert.Equal(TrainingRuntimeState.Ready, (await manager.CheckAsync()).State);
        Assert.Empty(Directory.GetDirectories(manager.Root, "stage-*"));
        await File.AppendAllTextAsync(TrainingRuntimeProvisioner.Options(manager.Current).BaseModelPath, "tampered");
        var invalid = await manager.CheckAsync();
        Assert.Equal(TrainingRuntimeState.Error, invalid.State);
        Assert.True(invalid.RequiresPreparation);
    }
    [Theory]
    [InlineData("hash")]
    [InlineData("manifest")]
    [InlineData("probe")]
    [InlineData("traversal")]
    public async Task FailedPreparationKeepsPreviousRuntime(string failure)
    {
        var source = NewSource(); var probe = new Probe(); var manager = Provisioner(source, probe);
        Assert.Equal(TrainingRuntimeState.Ready, (await manager.PrepareAsync()).State);
        var original = await File.ReadAllTextAsync(Path.Combine(manager.Current, "installed.json"));
        source.Corrupt = failure == "hash"; source.BadManifest = failure == "manifest"; source.Traversal = failure == "traversal";
        if (failure == "hash" && Directory.Exists(manager.Cache)) Directory.Delete(manager.Cache, true);
        probe.Fail = failure == "probe";
        var failed = await manager.PrepareAsync();
        Assert.Equal(TrainingRuntimeState.Error, failed.State);
        Assert.Contains(manager.LogPath, failed.Detail);
        Assert.Contains("Fallo en etapa", await File.ReadAllTextAsync(manager.LogPath));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(manager.Current, "installed.json")));
        Assert.Empty(Directory.GetDirectories(manager.Root, "stage-*"));
        probe.Fail = false;
        Assert.Equal(TrainingRuntimeState.Ready, (await manager.CheckAsync()).State);
    }
    [Fact]
    public async Task CancelAndRetryDoNotCommitPartialRuntime()
    {
        var source = NewSource(); var manager = Provisioner(source);
        using var cancel = new CancellationTokenSource(); source.Cancel = cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.PrepareAsync(ct: cancel.Token));
        Assert.False(Directory.Exists(manager.Current));
        Assert.Empty(Directory.GetDirectories(manager.Root, "stage-*"));
        var pending = await manager.CheckAsync();
        Assert.True(pending.CanResume);
        Assert.Contains("continuar", pending.Message, StringComparison.OrdinalIgnoreCase);
        source.Cancel = null;
        Assert.Equal(TrainingRuntimeState.Ready, (await manager.PrepareAsync()).State);
    }
    [Fact]
    public async Task VerifiedComponentCacheIsReusedAcrossPreparations()
    {
        var source = NewSource(); var manager = Provisioner(source);
        Assert.Equal(TrainingRuntimeState.Ready, (await manager.PrepareAsync()).State);
        var downloads = source.Downloads;
        Assert.True(downloads > 0);
        Assert.Equal(TrainingRuntimeState.Ready, (await manager.PrepareAsync()).State);
        Assert.Equal(downloads, source.Downloads);
    }
    [Fact]
    public async Task CheckingValidInstalledRuntimeDoesNotResolveOrDownloadAgain()
    {
        var source = NewSource(); var probe = new Probe(); var manager = Provisioner(source, probe);
        Assert.Equal(TrainingRuntimeState.Ready, (await manager.PrepareAsync()).State);
        var resolutions = source.Resolutions; var downloads = source.Downloads; var probes = probe.Calls;
        var receiptPath = Path.Combine(manager.Current, "installed.json");
        var receiptBefore = await File.ReadAllTextAsync(receiptPath);
        var nonEssential = Path.Combine(manager.Current, "python", "Lib", "site-packages", "fixture", "__init__.py");
        await File.AppendAllTextAsync(nonEssential, "# changed after validation");
        var progress = new Reports();
        var quick = await manager.CheckAsync(progress: progress);
        Assert.Equal(TrainingRuntimeState.Ready, quick.State);
        Assert.True(quick.CapabilitiesAreLastKnown);
        Assert.Equal(resolutions, source.Resolutions);
        Assert.Equal(downloads, source.Downloads);
        Assert.Equal(probes, probe.Calls);
        Assert.Equal(receiptBefore, await File.ReadAllTextAsync(receiptPath));
        Assert.DoesNotContain(progress.Items, item => item.Message.Contains("capacidad de procesamiento"));
        var deep = await manager.DeepCheckAsync();
        Assert.Equal(TrainingRuntimeState.Error, deep.State);
        Assert.Equal(probes, probe.Calls);
        Assert.Equal(downloads, source.Downloads);
        Assert.True(Directory.Exists(manager.Current));
    }
    [Fact]
    public async Task ReceiptPersistsValidatedIdentityAndLastKnownDevices()
    {
        var manager = Provisioner(NewSource(), new Probe { Devices = [Cpu(), Gpu(0)] });
        await manager.PrepareAsync();
        var receipt = await TrainingJson.ReadAsync<TrainingRuntimeReceipt>(Path.Combine(manager.Current, "installed.json"));
        Assert.Equal(TrainingRuntimeProvisioner.WorkerVersion, receipt.WorkerVersion);
        Assert.True(receipt.InstallationCompleted == true);
        Assert.NotNull(receipt.DeepValidatedAtUtc);
        Assert.NotNull(receipt.DevicesDetectedAtUtc);
        Assert.Equal(2, receipt.LastKnownDevices!.Count);
    }
    [Fact]
    public async Task LegacyReceiptRequestsOnlyBackgroundDeviceProbe()
    {
        var source = NewSource(); var probe = new Probe { Devices = [Cpu(), Gpu(0)] }; var manager = Provisioner(source, probe);
        await manager.PrepareAsync();
        var receiptPath = Path.Combine(manager.Current, "installed.json");
        var receipt = await TrainingJson.ReadAsync<TrainingRuntimeReceipt>(receiptPath);
        await TrainingJson.WriteAsync(receiptPath, receipt with
        {
            WorkerVersion = null, InstallationCompleted = null, DeepValidatedAtUtc = null,
            LastKnownDevices = null, DevicesDetectedAtUtc = null
        });
        var probes = probe.Calls; var resolutions = source.Resolutions; var downloads = source.Downloads;

        var quick = await manager.CheckAsync();

        Assert.Equal(TrainingRuntimeState.Ready, quick.State);
        Assert.True(quick.RequiresDeviceProbe);
        Assert.True(quick.CapabilitiesAreLastKnown);
        Assert.Equal(probes, probe.Calls);
        Assert.Equal(resolutions, source.Resolutions);
        Assert.Equal(downloads, source.Downloads);
    }
    [Fact]
    public async Task KnownGpuIsRestoredWithoutProbeAndDeviceProbePersistsFreshResult()
    {
        var source = NewSource(); var probe = new Probe { Devices = [Cpu(), Gpu(0)] }; var manager = Provisioner(source, probe);
        await manager.PrepareAsync();
        var probes = probe.Calls; var resolutions = source.Resolutions; var downloads = source.Downloads;

        var quick = await manager.CheckAsync();
        Assert.False(quick.RequiresDeviceProbe);
        Assert.Contains(quick.Devices, device => device.Available && device.Device.Kind == TrainingDeviceKind.Gpu);
        Assert.Equal(probes, probe.Calls);

        probe.Devices = [Cpu(), Gpu(1)];
        var current = await manager.ProbeDevicesAsync();
        Assert.True(current.DeviceAvailabilityConfirmed);
        Assert.Contains(current.Devices, device => device.Available && device.Device.DeviceId == 1);
        var receipt = await TrainingJson.ReadAsync<TrainingRuntimeReceipt>(Path.Combine(manager.Current, "installed.json"));
        Assert.Contains(receipt.LastKnownDevices!, device => device.Available && device.Device.DeviceId == 1);
        Assert.Equal(probes + 1, probe.Calls);
        Assert.Equal(resolutions, source.Resolutions);
        Assert.Equal(downloads, source.Downloads);
    }
    [Fact]
    public async Task FailedGpuProbeDisablesStaleGpuForSessionWithoutErasingReceipt()
    {
        var probe = new Probe { Devices = [Cpu(), Gpu(0)] }; var manager = Provisioner(NewSource(), probe);
        await manager.PrepareAsync();
        var receiptPath = Path.Combine(manager.Current, "installed.json");
        var receiptBefore = await File.ReadAllTextAsync(receiptPath);
        probe.Fail = true;

        var result = await manager.ProbeDevicesAsync();

        Assert.Equal(TrainingRuntimeState.Ready, result.State);
        Assert.True(result.DeviceProbeFailed);
        Assert.DoesNotContain(result.Devices, device => device.Device.Kind == TrainingDeviceKind.Gpu && device.Available);
        Assert.Contains(result.Devices, device => device.Device.Kind == TrainingDeviceKind.Cpu && device.Available);
        Assert.Equal(receiptBefore, await File.ReadAllTextAsync(receiptPath));
    }
    [Fact]
    public async Task SuccessfulProbeReportsCudaUnavailableReasonAndKeepsCpuSelectable()
    {
        var probe = new Probe
        {
            Devices = [Cpu(), new(new(TrainingDeviceKind.Gpu), false, UnavailableReason: "CUDA driver incompatible")]
        };
        var manager = Provisioner(NewSource(), probe);
        await manager.PrepareAsync();

        var result = await manager.ProbeDevicesAsync();

        Assert.True(result.DeviceAvailabilityConfirmed);
        Assert.Contains("CUDA driver incompatible", result.Detail);
        Assert.Contains(result.Devices, device => device.Device.Kind == TrainingDeviceKind.Cpu && device.Available);
        Assert.DoesNotContain(result.Devices, device => device.Device.Kind == TrainingDeviceKind.Gpu && device.Available);
    }
    [Fact]
    public async Task IncompatibleReceiptOrMissingEssentialFileDoesNotDownloadOrDeleteRuntime()
    {
        var source = NewSource(); var manager = Provisioner(source);
        await manager.PrepareAsync();
        var downloads = source.Downloads;
        var receiptPath = Path.Combine(manager.Current, "installed.json");
        var receipt = await TrainingJson.ReadAsync<TrainingRuntimeReceipt>(receiptPath);
        await TrainingJson.WriteAsync(receiptPath, receipt with { Manifest = receipt.Manifest with { Version = "incompatible" } });
        Assert.Equal(TrainingRuntimeState.Error, (await manager.CheckAsync()).State);
        Assert.Equal(downloads, source.Downloads);
        Assert.True(Directory.Exists(manager.Current));

        await TrainingJson.WriteAsync(receiptPath, receipt);
        File.Delete(Path.Combine(manager.Current, "worker.py"));
        Assert.Equal(TrainingRuntimeState.Error, (await manager.CheckAsync()).State);
        Assert.Equal(downloads, source.Downloads);
        Assert.True(File.Exists(receiptPath));
    }
    [Fact]
    public async Task ExplicitDeepCheckRunsFullIntegrityAndDeviceProbe()
    {
        var probe = new Probe(); var manager = Provisioner(NewSource(), probe);
        await manager.PrepareAsync();
        var calls = probe.Calls;
        var result = await manager.DeepCheckAsync();
        Assert.Equal(TrainingRuntimeState.Ready, result.State);
        Assert.Equal(calls + 1, probe.Calls);
        Assert.False(result.CapabilitiesAreLastKnown);
    }
    [Fact]
    public async Task MissingReceiptDoesNotDownloadOrRemoveInstalledRuntime()
    {
        var source = NewSource(); var manager = Provisioner(source);
        await manager.PrepareAsync();
        var downloads = source.Downloads;
        File.Delete(Path.Combine(manager.Current, "installed.json"));
        var result = await manager.CheckAsync();
        Assert.Equal(TrainingRuntimeState.Error, result.State);
        Assert.Equal(downloads, source.Downloads);
        Assert.True(File.Exists(Path.Combine(manager.Current, "python", "python.exe")));
    }
    [Fact]
    public async Task ActiveWorkerLeasePreventsReplacement()
    {
        var manager = Provisioner(NewSource());
        await manager.PrepareAsync();
        using var lease = manager.Acquire(false);
        await Assert.ThrowsAsync<IOException>(() => manager.PrepareAsync());
    }
    [Fact]
    public async Task MultipleGpusAndCpuRemainIndependentChoices()
    {
        var probe = new Probe { Devices = [Cpu(), Gpu(0), Gpu(1)] };
        var state = await Provisioner(NewSource(), probe).PrepareAsync();
        Assert.Equal(3, state.Devices.Count);
        foreach (var capability in state.Devices) TrainingDeviceSelection.RequireAvailable(capability.Device, state.Devices);
        var error = Assert.Throws<TrainingWorkerException>(() => TrainingDeviceSelection.RequireAvailable(Gpu(1).Device, [Cpu(), Gpu(0)]));
        Assert.Equal("training_gpu_unavailable", error.Code);
        TrainingDeviceSelection.RequireAvailable(Cpu().Device, [Cpu(), Gpu(0)]);
    }
    [Theory]
    [InlineData(TrainingDeviceKind.Cpu, 0, "cpu")]
    [InlineData(TrainingDeviceKind.Gpu, 1, "cuda:1")]
    public async Task ExplicitChoiceSurvivesReloadAndWorkerRequest(TrainingDeviceKind kind, int index, string expected)
    {
        var worker = new Worker { Devices = [Cpu(), Gpu(0), Gpu(1)] };
        await using var core = Core(worker);
        var project = await ProjectAsync(core);
        var selected = new TrainingDevice(kind, index, kind == TrainingDeviceKind.Cpu ? "CPU" : "Fixture GPU " + index);
        var job = await core.StartAsync(project.Id, TrainingProfile.Balanced, TrainingProfiles.BaseModel, device: selected);
        await core.WaitAsync(job.Id);
        var reloaded = await new JsonTrainingStore(Paths).LoadJobAsync(job.Id);
        Assert.Equal(selected, reloaded.SelectedDevice);
        Assert.Equal(expected, reloaded.Configuration.Device);
        Assert.Equal(TrainingProfile.Balanced, reloaded.Profile);
        Assert.Equal(42, reloaded.Configuration.Seed);
        Assert.NotNull(reloaded.Dataset);
        Assert.Equal(TrainingRuntimeProvisioner.Version, reloaded.RuntimeVersion);
        Assert.Equal("fixture-hash", reloaded.RuntimeManifestSha256);
        var request = PythonYoloTrainingWorker.CreateRequest(reloaded, "output", "base", "hash");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, TrainingJson.Options));
        Assert.Equal(expected, json.RootElement.GetProperty("configuration").GetProperty("device").GetString());
        Assert.Equal(selected, worker.Received!.SelectedDevice);
    }
    [Fact]
    public async Task DisappearedGpuPreventsJobAndNeverFallsBack()
    {
        var worker = new Worker { Devices = [Cpu()] };
        await using var core = Core(worker); var project = await ProjectAsync(core);
        await Assert.ThrowsAsync<TrainingWorkerException>(() => core.StartAsync(project.Id, TrainingProfile.Quick, TrainingProfiles.BaseModel, device: Gpu(0).Device));
        Assert.Empty(await core.ListJobsAsync()); Assert.Null(worker.Received);
        await Assert.ThrowsAsync<ArgumentException>(() => core.StartAsync(project.Id, TrainingProfile.Quick, TrainingProfiles.BaseModel));
    }
    [Fact]
    public async Task GpuFailureDuringJobIsFailedWithoutCpuRestart()
    {
        var worker = new Worker { Devices = [Cpu(), Gpu(0)], FailAtRun = true };
        await using var core = Core(worker); var project = await ProjectAsync(core);
        var job = await core.StartAsync(project.Id, TrainingProfile.Quick, TrainingProfiles.BaseModel, device: Gpu(0).Device);
        await core.WaitAsync(job.Id);
        var failed = await core.GetJobAsync(job.Id);
        Assert.Equal(TrainingJobStatus.Failed, failed.Status);
        Assert.Equal("training_gpu_unavailable", failed.Error!.ErrorCode);
        Assert.Equal("cuda:0", failed.Configuration.Device); Assert.Equal(1, worker.Runs);
    }
    [Theory]
    [InlineData("torch-2.14.1+cu130-cp311-cp311-win_amd64.whl", true)]
    [InlineData("module-1.0-cp37-abi3-win_amd64.whl", true)]
    [InlineData("module-1.0-py2.py3-none-any.whl", true)]
    [InlineData("module-1.0-cp312-cp312-win_amd64.whl", false)]
    [InlineData("module-1.0-cp311-cp311-manylinux_x86_64.whl", false)]
    public void OnlyCompatibleBinaryWheelsAreAccepted(string filename, bool accepted) =>
        Assert.Equal(accepted, OfficialTrainingRuntimeSource.CompatibleWheel(filename));

    [Theory]
    [InlineData("2.9.0.post0", true)]
    [InlineData("3.11.9", true)]
    [InlineData("1.0rc2", true)]
    [InlineData("latest", false)]
    [InlineData("1.*", false)]
    [InlineData("1.0+local", false)]
    public void ExactVersionContractAcceptsPinnedPep440Versions(string version, bool accepted) =>
        Assert.Equal(accepted, OfficialTrainingRuntimeSource.IsExactVersion(version));

    [Fact]
    public void OfficialManifestRejectsIncompletePlaceholderAndInvalidUrlContracts()
    {
        var valid = OfficialManifest();
        OfficialTrainingRuntimeSource.ValidateOfficialManifest(valid);
        Assert.Throws<InvalidDataException>(() => OfficialTrainingRuntimeSource.ValidateOfficialManifest(
            valid with { Artifacts = valid.Artifacts.Where(a => !a.Name.StartsWith("torchvision-")).ToArray() }));
        Assert.Throws<InvalidDataException>(() => OfficialTrainingRuntimeSource.ValidateOfficialManifest(valid with
        { Artifacts = valid.Artifacts.Select((a, i) => i == 0 ? a with { Sha256 = new string('0', 64) } : a).ToArray() }));
        Assert.Throws<InvalidDataException>(() => OfficialTrainingRuntimeSource.ValidateOfficialManifest(valid with
        { Artifacts = valid.Artifacts.Select((a, i) => i == 0 ? a with { Url = "https://example.invalid/python.zip" } : a).ToArray() }));
        Assert.Throws<InvalidDataException>(() => OfficialTrainingRuntimeSource.ValidateOfficialManifest(valid with
        { Artifacts = valid.Artifacts.Select((a, i) => i == 0 ? a with { Url = "" } : a).ToArray() }));
        Assert.Throws<InvalidDataException>(() => OfficialTrainingRuntimeSource.ValidateOfficialManifest(valid with
        { Artifacts = valid.Artifacts.Select((a, i) => i == 0 ? a with { Url = "https://www.python.org/incorrect-python.zip" } : a).ToArray() }));
    }

    [Fact]
    public async Task Http404IsReportedWithOperationStatusAndUrl()
    {
        using var http = new HttpClient(new ResponseHandler(System.Net.HttpStatusCode.NotFound));
        var source = new OfficialTrainingRuntimeSource(Assets, http);
        var artifact = new TrainingRuntimeArtifact("component.whl", "https://files.pythonhosted.org/component.whl",
            Convert.ToHexString(SHA256.HashData([1])), 1, "wheel");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => source.DownloadAsync(artifact,
            Path.Combine(_root, "component.whl"), _ => { }, default));
        Assert.Contains("HTTP 404", error.Message);
        Assert.Contains(artifact.Url, error.Message);
        Assert.False(File.Exists(Path.Combine(_root, "component.whl")));
    }

    [Fact]
    public async Task InterruptedArtifactResumesFromPersistedByteWithValidator()
    {
        byte[] all = [1, 2, 3, 4, 5, 6];
        var destination = Path.Combine(_root, "cache", "component.whl");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination + ".partial", all[..3]);
        var artifact = new TrainingRuntimeArtifact("component.whl", "https://files.pythonhosted.org/component.whl",
            Convert.ToHexString(SHA256.HashData(all)), all.Length, "wheel");
        await TrainingJson.WriteAsync(destination + ".download.json", new
        {
            url = artifact.Url, sha256 = artifact.Sha256, size = artifact.Size, e_tag = "\"fixture-v1\"", last_modified = (DateTimeOffset?)null
        });
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"fixture-v1\"", request.Headers.IfRange!.EntityTag!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(all[3..]) };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });
        await new OfficialTrainingRuntimeSource(Assets, new HttpClient(handler)).DownloadAsync(artifact, destination, _ => { }, default);
        Assert.Equal(all, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".partial"));
        Assert.False(File.Exists(destination + ".download.json"));
    }

    [Fact]
    public async Task ServerWithoutRangeRestartsOnlyAffectedArtifact()
    {
        byte[] all = [7, 8, 9, 10];
        var destination = Path.Combine(_root, "cache", "component.whl");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination + ".partial", all[..2]);
        var artifact = new TrainingRuntimeArtifact("component.whl", "https://files.pythonhosted.org/component.whl",
            Convert.ToHexString(SHA256.HashData(all)), all.Length, "wheel");
        await TrainingJson.WriteAsync(destination + ".download.json", new
        {
            url = artifact.Url, sha256 = artifact.Sha256, size = artifact.Size, e_tag = "\"old\"", last_modified = (DateTimeOffset?)null
        });
        var handler = new DelegateHandler(request =>
        {
            Assert.NotNull(request.Headers.Range);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(all) };
        });
        await new OfficialTrainingRuntimeSource(Assets, new HttpClient(handler)).DownloadAsync(artifact, destination, _ => { }, default);
        Assert.Equal(all, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task CancellationKeepsRecoverablePartialStateOnDisk()
    {
        byte[] all = [1, 2, 3, 4, 5, 6];
        var destination = Path.Combine(_root, "cache", "component.whl");
        var artifact = new TrainingRuntimeArtifact("component.whl", "https://files.pythonhosted.org/component.whl",
            Convert.ToHexString(SHA256.HashData(all)), all.Length, "wheel");
        var handler = new DelegateHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new CancelAfterFirstReadStream(all)) };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            return response;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OfficialTrainingRuntimeSource(Assets, new HttpClient(handler)).DownloadAsync(artifact, destination, _ => { }, default));
        Assert.Equal(2, new FileInfo(destination + ".partial").Length);
        Assert.True(File.Exists(destination + ".download.json"));
        Assert.False(File.Exists(destination));
    }

    private static TrainingDeviceCapability Cpu() => new(new(TrainingDeviceKind.Cpu), true);
    private static TrainingDeviceCapability Gpu(int index) => new(new(TrainingDeviceKind.Gpu, index, "Fixture GPU " + index), true, 4096);
    private TrainingApplicationService Core(Worker worker) => new(new JsonTrainingStore(Paths), new TrainingAssetService(Paths), new Exporter(), worker, new Publisher(), new TrainingResourceMonitor());
    private async Task<TrainingProject> ProjectAsync(TrainingApplicationService core)
    {
        var project = await core.CreateProjectAsync("Aves", "", ["Ave"]);
        project = project with { Images = Enumerable.Range(0, 3).Select(i => new TrainingImage(Guid.NewGuid(), project.Id, "x", "x", 10, 10,
            DateTimeOffset.UtcNow, TrainingSourceType.Image, "hash", null, null, [new(project.Classes[0].Id, .5, .5, .2, .2)], true)).ToArray() };
        await new JsonTrainingStore(Paths).SaveProjectAsync(project); return project;
    }
    private sealed class Worker : ITrainingWorker, ITrainingDeviceValidator
    {
        public IReadOnlyList<TrainingDeviceCapability> Devices = [];
        public TrainingJob? Received; public bool FailAtRun; public int Runs;
        public Task<TrainingRuntimeIdentity> ValidateDeviceAsync(TrainingDevice device, CancellationToken ct)
        { TrainingDeviceSelection.RequireAvailable(device, Devices); return Task.FromResult(new TrainingRuntimeIdentity(TrainingRuntimeProvisioner.Version, "fixture-hash")); }
        public async Task<TrainingResult> RunAsync(TrainingJob job, Func<TrainingProgress, Task> progress, CancellationToken ct)
        {
            Received = job; Runs++;
            if (FailAtRun) throw new TrainingWorkerException("training_gpu_unavailable", "La GPU seleccionada ya no está disponible.");
            await progress(new(TrainingJobStatus.Validating, 80)); await progress(new(TrainingJobStatus.Exporting, 90));
            return new("model", "2.0.0", job.Configuration.Device, new Dictionary<string, double>());
        }
    }
    private sealed class Exporter : ITrainingDatasetExporter
    { public Task<TrainingDataset> ExportAsync(TrainingJob job, CancellationToken ct) => Task.FromResult(new TrainingDataset("snapshot", "dataset.yaml", [], [], [])); }
    private sealed class Publisher : ITrainingModelPublisher
    {
        public ModelDescriptor? FindPublishedModel(Guid jobId) => null;
        public Task<ModelDescriptor> PublishAsync(TrainingJob job, CancellationToken ct) => Task.FromResult(new ModelDescriptor("id", "Model", "1", ModelFormat.Onnx, [], 640, 640, "model"));
    }
    private sealed class Probe : ITrainingRuntimeProbe, ITrainingRuntimeDeviceProbe
    {
        public int Calls; public bool Fail;
        public IReadOnlyList<TrainingDeviceCapability> Devices = [Cpu(), new(new(TrainingDeviceKind.Gpu), false)];
        public Task<IReadOnlyList<TrainingDeviceCapability>> ProbeAsync(TrainingRuntimeOptions options, CancellationToken ct)
        { Calls++; options.Validate(); if (Fail) throw new InvalidDataException("fixture probe failure"); return Task.FromResult(Devices); }
        public Task<IReadOnlyList<TrainingDeviceCapability>> ProbeDevicesAsync(TrainingRuntimeOptions options, CancellationToken ct) =>
            ProbeAsync(options, ct);
    }
    private sealed class Reports : IProgress<TrainingRuntimeProgress>
    { public List<TrainingRuntimeProgress> Items = []; public void Report(TrainingRuntimeProgress value) => Items.Add(value); }
    private sealed class ResponseHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
    }
    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
    private sealed class CancelAfterFirstReadStream(byte[] data) : MemoryStream(data)
    {
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_read) throw new OperationCanceledException("Simulated application shutdown.");
            var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
            _read = true;
            return read;
        }
    }
    private sealed class Source(string assets) : ITrainingRuntimeSource
    {
        public int Resolutions, Downloads; public bool Corrupt, BadManifest, Traversal; public CancellationTokenSource? Cancel;
        private readonly Dictionary<string, byte[]> _bytes = [];
        public async Task<TrainingRuntimeManifest> ResolveAsync(CancellationToken ct)
        {
            Resolutions++;
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                using var writer = new StreamWriter(zip.CreateEntry(Traversal ? "../escape" : "python.exe").Open()); writer.Write("fixture executable");
            }
            _bytes["python.zip"] = memory.ToArray(); _bytes["yolo26n.pt"] = [1, 2, 3];
            using var wheelMemory = new MemoryStream();
            using (var wheel = new ZipArchive(wheelMemory, ZipArchiveMode.Create, true))
            { using var writer = new StreamWriter(wheel.CreateEntry("fixture/__init__.py").Open()); writer.Write("# fixture"); }
            _bytes["fixture-1.0.0-py3-none-any.whl"] = wheelMemory.ToArray();
            var artifacts = _bytes.Select(x => new TrainingRuntimeArtifact(x.Key, "https://fixture.invalid/" + x.Key,
                Convert.ToHexString(SHA256.HashData(x.Value)), x.Value.Length, x.Key.EndsWith("zip") ? "python" : "model")).ToArray();
            artifacts = artifacts.Select(a => a.Name.EndsWith(".whl") ? a with { Kind = "wheel", Url = "https://files.pythonhosted.org/fixture.whl" }
                : a with { Url = a.Kind == "python" ? OfficialTrainingRuntimeSource.PythonUrl : "https://github.com/fixture/model" }).ToArray();
            return new(BadManifest ? "invalid" : TrainingRuntimeProvisioner.Version,
                await TrainingRuntimeProvisioner.ComputeRecipeHashAsync(assets, ct), artifacts);
        }
        public async Task DownloadAsync(TrainingRuntimeArtifact artifact, string destination, Action<long> bytes, CancellationToken ct)
        {
            Downloads++;
            Cancel?.Cancel(); ct.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(destination, Corrupt ? [0] : _bytes[artifact.Name], ct);
            bytes(_bytes[artifact.Name].Length);
        }
    }

    private static TrainingRuntimeManifest OfficialManifest()
    {
        string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        TrainingRuntimeArtifact Artifact(string name, string kind, string host = "files.pythonhosted.org") =>
            new(name, $"https://{host}/{name}", Hash(name), 100, kind);
        return new(TrainingRuntimeProvisioner.Version, Hash("recipe"),
        [
            new("python.zip", OfficialTrainingRuntimeSource.PythonUrl, OfficialTrainingRuntimeSource.PythonSha256, 11243893, "python"),
            Artifact("torch-2.14.1+cu130-cp311-cp311-win_amd64.whl", "wheel", "download.pytorch.org"),
            Artifact("torchvision-0.29.1+cu130-cp311-cp311-win_amd64.whl", "wheel", "download.pytorch.org"),
            Artifact("ultralytics-8.4.170-py3-none-any.whl", "wheel"),
            Artifact("onnx-1.23.1-cp311-cp311-win_amd64.whl", "wheel"),
            Artifact("yolo26n.pt", "model", "github.com")
        ]);
    }
}
