using System.IO;
using System.Diagnostics;
using System.Windows;
using HandRaise.Desktop.Controls;
using HandRaise.Desktop.Services;
using HandRaise.Desktop.ViewModels;
using HandRaise.Domain.Training;
using HandRaise.Infrastructure.Windows.Inference;
using HandRaise.Infrastructure.Windows.Training;

namespace HandRaise.Desktop.Tests;

public sealed class TrainingLabDesktopTests
{
    [Fact]
    public async Task TrainingDeviceRequiresExplicitChoiceAndCpuRemainsSelectableWithGpu()
    {
        await using var host = new NodeHostController();
        await using var vm = new TrainingLabViewModel(host, () => { });
        var cpu = new TrainingDeviceCapability(new(TrainingDeviceKind.Cpu), true);
        var gpu = new TrainingDeviceCapability(new(TrainingDeviceKind.Gpu, 1, "GPU fixture"), true);
        vm.Devices.Add(cpu); vm.Devices.Add(gpu);
        Assert.Null(vm.SelectedDevice);
        vm.SelectedDevice = gpu; Assert.Same(gpu, vm.SelectedDevice);
        vm.UseCpuCommand.Execute(null); Assert.Same(cpu, vm.SelectedDevice);
        vm.SelectedDevice = new(new(TrainingDeviceKind.Gpu), false);
        Assert.Same(cpu, vm.SelectedDevice);
    }
    [Fact]
    public void ClassReorderingAndRemovalNeverReassignExistingBoxes()
    {
        TrainingClass[] classes = [new(Guid.NewGuid(), "Pollo"), new(Guid.NewGuid(), "Gallo")];
        var reordered = TrainingClassEdits.Apply(classes, ["Gallo", "Pollo"]);
        Assert.Equal(classes[1].Id, reordered[0].Id);
        Assert.Equal(classes[0].Id, reordered[1].Id);
        var removed = TrainingClassEdits.Apply(classes, ["Gallo"]);
        Assert.Equal(classes[1].Id, Assert.Single(removed).Id);
        Assert.Throws<ArgumentException>(() => new BoundingBoxAnnotation(classes[0].Id, .5, .5, .1, .1).Validate(removed));
        Assert.Equal(classes[0].Id, TrainingClassEdits.Apply(classes, ["Pollito", "Gallo"])[0].Id);
    }
    [Fact]
    public void LetterboxedCoordinatesRoundTripAndClamp()
    {
        var image = TrainingBoxCoordinates.ImageRect(new Size(800, 600), 1600, 800);
        Assert.Equal(new Rect(0, 100, 800, 400), image);
        var a = TrainingBoxCoordinates.Normalize(new Point(-5, 90), image);
        var b = TrainingBoxCoordinates.Normalize(new Point(900, 550), image);
        var box = TrainingBoxCoordinates.FromCorners(Guid.NewGuid(), a, b)!;
        Assert.Equal(image, TrainingBoxCoordinates.ToRect(box, image));
        box.Validate([new(box.ClassId, "Ave")]);
        Assert.Null(TrainingBoxCoordinates.FromCorners(box.ClassId, a, a));
    }

    [Theory]
    [InlineData(TrainingJobStatus.Training, "Entrenando")]
    [InlineData(TrainingJobStatus.Cancelled, "Cancelado")]
    [InlineData(TrainingJobStatus.Registering, "Publicando")]
    [InlineData(TrainingJobStatus.Completed, "Listo")]
    public void ProgressUsesHumanStates(TrainingJobStatus status, string label) => Assert.Equal(label, TrainingLabViewModel.HumanState(status));

    [Fact]
    public async Task ControllerOwnsOneCoreAndSameRegistryAcrossHostStops()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-desktop-training-" + Guid.NewGuid().ToString("N"));
        var paths = new TrainingPaths(root);
        try
        {
            var registry = new StandardModelRegistry(paths.CustomModels);
            await using (var host = new NodeHostController(registry, paths))
            {
                var first = host.GetTrainingAsync(); var second = host.GetTrainingAsync();
                Assert.Same(first, second);
                var core = await first;
                var project = await core.CreateProjectAsync("Aves", "", ["Pollo", "Gallo"]);
                await host.StopAsync(); // no server is started by this test
                Assert.Same(core, await host.GetTrainingAsync());
                Assert.Same(registry, host.ModelRegistry);
                var reloaded = await new JsonTrainingStore(paths).LoadProjectAsync(project.Id);
                Assert.Equal(project.Classes, reloaded.Classes);
            }
            await using var reopened = await TrainingCore.CreateAsync(new StandardModelRegistry(paths.CustomModels), paths);
            Assert.Single(await reopened.ListProjectsAsync()); // previous owner released its lock
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingProjectsLoadWhileRuntimeCheckIsStillRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-desktop-project-load-" + Guid.NewGuid().ToString("N"));
        var paths = new TrainingPaths(root); var runtime = new PendingRuntime();
        try
        {
            await using var host = new NodeHostController(new StandardModelRegistry(paths.CustomModels), paths);
            var core = await host.GetTrainingAsync();
            await core.CreateProjectAsync("desodorantes", "", ["desodorante"]);
            await using var vm = new TrainingLabViewModel(host, () => { }, paths, runtime);

            await vm.InitializeAsync();
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("desodorantes", Assert.Single(vm.Projects).Name);
            Assert.True(vm.IsCheckingRuntime);
            Assert.False(vm.NeedsPreparation);
            Assert.True(vm.CanSelectProject);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RuntimeFailureDoesNotHideOrDisableExistingProjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-desktop-runtime-failure-" + Guid.NewGuid().ToString("N"));
        var paths = new TrainingPaths(root);
        try
        {
            await using var host = new NodeHostController(new StandardModelRegistry(paths.CustomModels), paths);
            var core = await host.GetTrainingAsync();
            await core.CreateProjectAsync("desodorantes", "", ["desodorante"]);
            await using var vm = new TrainingLabViewModel(host, () => { }, paths, new FailedRuntime());

            await vm.InitializeAsync();
            await vm.RuntimeCheckTask;

            Assert.Equal("desodorantes", Assert.Single(vm.Projects).Name);
            Assert.False(vm.NeedsPreparation);
            Assert.True(vm.CanSelectProject);
            Assert.Contains("validar", vm.Runtime, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LegacyReceiptChecksDevicesWithoutDeclaringGpuUnavailableAndUpdatesSelector()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-desktop-device-probe-" + Guid.NewGuid().ToString("N"));
        var paths = new TrainingPaths(root); var runtime = new DeviceProbeRuntime();
        try
        {
            await using var host = new NodeHostController(new StandardModelRegistry(paths.CustomModels), paths);
            await using var vm = new TrainingLabViewModel(host, () => { }, paths, runtime);
            await vm.InitializeAsync();
            await vm.RuntimeCheckTask;
            await runtime.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsCheckingDevices);
            Assert.False(vm.GpuUnavailable);
            Assert.Contains("Comprobando", vm.Runtime, StringComparison.OrdinalIgnoreCase);

            runtime.ProbeResult.TrySetResult(new(TrainingRuntimeState.Ready,
                [new(new(TrainingDeviceKind.Cpu), true), new(new(TrainingDeviceKind.Gpu, 0, "NVIDIA GeForce RTX 2050"), true)],
                "GPU disponible — NVIDIA GeForce RTX 2050", DeviceAvailabilityConfirmed: true));
            await vm.DeviceCheckTask;

            Assert.False(vm.IsCheckingDevices);
            Assert.False(vm.GpuUnavailable);
            Assert.Contains(vm.Devices, device => device.Available && device.Device.DisplayName == "NVIDIA GeForce RTX 2050");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class PendingRuntime : ITrainingRuntimeManager
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PreparationPath => "pending";
        public async Task<TrainingRuntimeStatus> CheckAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); }
        public Task<TrainingRuntimeStatus> ProbeDevicesAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null) =>
            throw new NotSupportedException();
        public Task<TrainingRuntimeStatus> DeepCheckAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null) =>
            throw new NotSupportedException();
        public Task<TrainingRuntimeStatus> PrepareAsync(IProgress<TrainingRuntimeProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
    private sealed class FailedRuntime : ITrainingRuntimeManager
    {
        public string PreparationPath => "failed";
        public Task<TrainingRuntimeStatus> CheckAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null) =>
            Task.FromResult(new TrainingRuntimeStatus(TrainingRuntimeState.Error, [], "No se pudo validar el entorno."));
        public Task<TrainingRuntimeStatus> ProbeDevicesAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null) =>
            throw new NotSupportedException();
        public Task<TrainingRuntimeStatus> DeepCheckAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null) =>
            throw new NotSupportedException();
        public Task<TrainingRuntimeStatus> PrepareAsync(IProgress<TrainingRuntimeProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
    private sealed class DeviceProbeRuntime : ITrainingRuntimeManager
    {
        public string PreparationPath => "device-probe";
        public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TrainingRuntimeStatus> ProbeResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TrainingRuntimeStatus> CheckAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null) =>
            Task.FromResult(new TrainingRuntimeStatus(TrainingRuntimeState.Ready,
                [new(new TrainingDevice(TrainingDeviceKind.Cpu), true)], "Entorno listo. Comprobando dispositivos disponibles...",
                CapabilitiesAreLastKnown: true, RequiresDeviceProbe: true));
        public Task<TrainingRuntimeStatus> ProbeDevicesAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null)
        { ProbeStarted.TrySetResult(); return ProbeResult.Task.WaitAsync(ct); }
        public Task<TrainingRuntimeStatus> DeepCheckAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null) =>
            throw new NotSupportedException();
        public Task<TrainingRuntimeStatus> PrepareAsync(IProgress<TrainingRuntimeProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
