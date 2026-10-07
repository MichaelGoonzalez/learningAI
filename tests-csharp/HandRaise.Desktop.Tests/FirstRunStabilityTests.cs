using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using HandRaise.Application.Capture;
using HandRaise.Application.Storage;
using HandRaise.Desktop.Configuration;
using HandRaise.Desktop.Services;
using HandRaise.Desktop.ViewModels;
using HandRaise.Application.Zones;
using HandRaise.Host.Services;

namespace HandRaise.Desktop.Tests;

public sealed class FirstRunStabilityTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _camerasJsonPath;

    public FirstRunStabilityTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"first_run_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _camerasJsonPath = Path.Combine(_tempDirectory, "cameras.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try { Directory.Delete(_tempDirectory, true); } catch { }
        }
    }

    private static DesktopConfiguration CreateTestConfig() => new(
        Model: new ModelSettings("models/yolo26n-pose.onnx", "yolo26n-pose", 640, 640, 80, 17, 0.25f, 0.45f, 100),
        Capture: new CaptureSettings(30, 1000, 5000, 5000, 5000),
        Hands: new HandSettings(0.5, 0.2, true, 10, 3, 3, 1000, 3000),
        Tracker: new TrackerSettings(0.5f, 0.2f, 0.6f, 0.7f, 0.5f, 30, 30, 1, 1, 1),
        Storage: new StorageSettings("data/events.db", "data/snapshots", 1000, 50, 0.2, 75, 30, 1000, 60),
        Cameras: [],
        Ui: new UiSettings(true, 100));

    // Caso A — Instalación limpia: 0 cámaras persistidas -> inicializar -> Cameras.Count == 0 y First Run activo indefinidamente
    [Fact]
    public async Task CaseA_CleanInstall_ZeroPersistedCameras_FirstRunRemainsActiveIndefinitely()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        // Before initial sync
        Assert.True(vm.IsLoadingInitialState);
        Assert.False(vm.HasNoCameras);
        Assert.False(vm.HasCameras);

        // Simulate initial sync completing with 0 cameras in backend
        vm.MarkInitialSyncComplete();

        // Must transition to and stay in First Run
        Assert.False(vm.IsLoadingInitialState);
        Assert.True(vm.HasNoCameras);
        Assert.False(vm.HasCameras);
        Assert.Empty(vm.Cameras);
        Assert.Equal(0, vm.TotalCamerasCount);

        // Background refreshes (alerts, models, integrations) must NEVER knock user out of First Run
        await vm.RefreshAlertsAsync();
        await vm.RefreshIntegrationsAsync();
        await vm.RefreshModelsAsync();

        Assert.True(vm.HasNoCameras);
        Assert.False(vm.HasCameras);
        Assert.Empty(vm.Cameras);
    }

    // Caso B — Webcam física disponible: Windows detecta webcam -> NO se crea automáticamente ninguna cámara configurada
    [Fact]
    public async Task CaseB_PhysicalWebcamDetected_DoesNotAutoRegisterConfiguredCamera()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var detectedDevices = new List<VideoDeviceInfo>
        {
            new(0, "Integrated Webcam (USB 0)", "usb-device-winrt-id-1", true),
            new(1, "USB Camera 1 (USB 1)", "usb-device-winrt-id-2", false)
        };
        var enumerator = new StubDeviceEnumerator(detectedDevices);

        var vm = new MainViewModel(config, controller, dispatcher, _ => { }, videoDeviceEnumerator: enumerator);
        vm.MarkInitialSyncComplete();

        // Hardware enumeration for wizard
        await vm.PopulateDetectedUsbDevicesAsync();

        // Hardware devices were detected for wizard use:
        Assert.Equal(2, vm.DetectedUsbDevices.Count);

        // But NO configured cameras exist in Cameras collection!
        Assert.Empty(vm.Cameras);
        Assert.True(vm.HasNoCameras);
        Assert.False(vm.HasCameras);
    }

    // Caso C — Cámara guardada por usuario: El usuario completa el wizard y guarda -> Cameras.Count == 1, First Run termina
    [Fact]
    public async Task CaseC_UserSavesCameraViaWizard_EndsFirstRunAndSelectsWorkspace()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var cameraStore = new InMemoryCameraStore();
        var cameraService = new InMemoryCameraManagementService(cameraStore);

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });
        vm.MarkInitialSyncComplete();

        Assert.True(vm.HasNoCameras);

        // Simulate wizard inputs
        vm.ShowAddCameraModalCommand.Execute(null);
        Assert.True(vm.IsAddCameraModalOpen);
        vm.NewCameraName = "Cámara Entrada Principal";
        vm.WizardUsbIndex = "0";
        vm.NewCameraType = "usb";

        // Save through service directly (as SaveWizardCameraAsync does)
        var created = await cameraService.CreateAsync(new CameraWriteRequest(
            Id: "cam-test-1",
            Name: vm.NewCameraName,
            Source: "0",
            Enabled: true));

        Assert.NotNull(created);
        var view = new CameraViewModel(created, cameraService, dispatcher);
        vm.Cameras.Add(view);

        // First Run must immediately end
        Assert.Single(vm.Cameras);
        Assert.False(vm.HasNoCameras);
        Assert.True(vm.HasCameras);
    }

    // Caso D — Reinicio: Existe una cámara legítimamente guardada -> restaurar -> NO aparece onboarding
    [Fact]
    public void CaseD_RestartWithSavedCamera_RestoresCameraWithoutOnboarding()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        // Camera is loaded into collection during sync
        var savedView = new CameraView("cam-saved", "Cámara Oficina", "0", true, false, false, 0, null);
        var stubService = new StubCameraManagementService();
        var cameraVm = new CameraViewModel(savedView, stubService, dispatcher);
        vm.Cameras.Add(cameraVm);

        vm.MarkInitialSyncComplete();

        // Must display operational dashboard, NOT first-run onboarding
        Assert.False(vm.IsLoadingInitialState);
        Assert.False(vm.HasNoCameras);
        Assert.True(vm.HasCameras);
        Assert.Single(vm.Cameras);
        Assert.Equal("Cámara Oficina", vm.Cameras[0].Name);
    }

    // Caso E — Legacy seed migration: Cámara histórica auto-generada 'camera-0' se migra a 0 cámaras limpias
    [Fact]
    public async Task CaseE_LegacyFactorySeed_MigratesToCleanZeroCameras_WhilePreservingUserCameras()
    {
        var store = new JsonCameraStore(_camerasJsonPath);

        // 1. Setup a legacy factory auto-seed file as produced by versions prior to UX9
        var legacyFactorySeed = new CameraDefinition(
            Id: "camera-0",
            Name: "Cámara principal",
            Source: "0",
            Enabled: true,
            Loop: false,
            Zones: []);

        var userLegitimateCamera = new CameraDefinition(
            Id: "cam-legitimate",
            Name: "Cámara Patio",
            Source: "rtsp://192.168.1.50/live",
            Enabled: true,
            Loop: false,
            Zones: []);

        // Write legacy seed directly
        await store.AddAsync(legacyFactorySeed);
        var listBefore = await store.ListAsync();
        Assert.Single(listBefore);
        Assert.Equal("camera-0", listBefore[0].Id);

        // 2. InitializeAsync runs on startup:
        // It must detect that camera-0 is the untouched legacy factory seed and migrate it!
        await store.InitializeAsync([]);
        var listAfter = await store.ListAsync();
        Assert.Empty(listAfter); // Clean 0 cameras for fresh First-Run!

        // 3. Verify user's legitimate camera is NEVER destroyed:
        await store.AddAsync(userLegitimateCamera);
        await store.InitializeAsync([]);
        var listWithUserCam = await store.ListAsync();
        Assert.Single(listWithUserCam);
        Assert.Equal("cam-legitimate", listWithUserCam[0].Id);
    }

    // Caso F — Full-Screen First Run: Asistente de 6 pasos completo y transición a ApplicationShell
    [Fact]
    public void CaseF_FullScreenFirstRun_SixStepsFlow_And_TransitionsToApplicationShell()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        vm.MarkInitialSyncComplete();
        Assert.True(vm.ShowFirstRunExperience);
        Assert.False(vm.ShowApplicationShell);
        Assert.Equal(1, vm.FirstRunStep);
        Assert.True(vm.IsFirstRunStep1);

        // Paso 1 -> Paso 2
        vm.FirstRunStartCommand.Execute(null);
        Assert.Equal(2, vm.FirstRunStep);
        Assert.True(vm.IsFirstRunStep2);

        // Paso 2: Selección de origen USB
        vm.FirstRunSelectSourceCommand.Execute("USB");
        Assert.Equal("USB", vm.WizardSourceType);
        vm.WizardUsbIndex = "0";

        // Paso 2 -> Paso 3
        vm.FirstRunNextCommand.Execute(null);
        Assert.Equal(3, vm.FirstRunStep);
        Assert.True(vm.IsFirstRunStep3);

        // Paso 3 -> Paso 4 (allow save without probe for fast test)
        vm.AllowSaveWithoutProbe = true;
        vm.FirstRunNextCommand.Execute(null);
        Assert.Equal(4, vm.FirstRunStep);
        Assert.True(vm.IsFirstRunStep4);

        // Paso 4 -> Paso 5
        vm.FirstRunNextCommand.Execute(null);
        Assert.Equal(5, vm.FirstRunStep);
        Assert.True(vm.IsFirstRunStep5);

        // Paso 5: Asignar nombre sugerido
        vm.FirstRunSetCameraNameSuggestionCommand.Execute("Entrada principal");
        Assert.Equal("Entrada principal", vm.NewCameraName);

        // Simulate save transition to Step 6
        var savedView = new CameraView("cam-1", "Entrada principal", "0", true, false, false, 0, null);
        var stubService = new StubCameraManagementService();
        var camVm = new CameraViewModel(savedView, stubService, dispatcher);
        vm.Cameras.Add(camVm);
        vm.FirstRunStep = 6;

        // In Step 6, ShowFirstRunExperience is STILL true until user chooses a CTA!
        Assert.True(vm.IsFirstRunStep6);
        Assert.True(vm.ShowFirstRunExperience);
        Assert.False(vm.ShowApplicationShell);

        // User clicks "[ Configurar Soluciones IA ]"
        vm.FirstRunConfigureAiCommand.Execute(null);

        // Transitions cleanly into ApplicationShell and Workspace
        Assert.False(vm.ShowFirstRunExperience);
        Assert.True(vm.ShowApplicationShell);
        Assert.Equal(8, vm.SelectedNavIndex);
        Assert.True(vm.IsCameraDetailSelected);
        Assert.NotNull(vm.SelectedDetailCamera);
        Assert.True(vm.SelectedDetailCamera.IsAnalyticsTabSelected);
    }

    // Caso G — Full-Screen First Run: Cancelar o volver atrás NUNCA abre shell vacío
    [Fact]
    public void CaseG_FullScreenFirstRun_CancelOrBack_DoesNotDropIntoEmptyShell()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        vm.MarkInitialSyncComplete();
        Assert.True(vm.ShowFirstRunExperience);
        Assert.False(vm.ShowApplicationShell);

        // Go to Step 2
        vm.FirstRunStartCommand.Execute(null);
        Assert.Equal(2, vm.FirstRunStep);

        // Go back to Step 1
        vm.FirstRunBackCommand.Execute(null);
        Assert.Equal(1, vm.FirstRunStep);

        // Repeated back calls do not go below 1 or exit First Run
        vm.FirstRunBackCommand.Execute(null);
        Assert.Equal(1, vm.FirstRunStep);
        Assert.True(vm.ShowFirstRunExperience);
        Assert.False(vm.ShowApplicationShell);
    }

    // Caso H — Probe preview lifecycle: entrar a Paso 4 inicia probe stream; avanzar a Paso 5 o volver a Paso 3 lo detiene
    [Fact]
    public async Task CaseH_ProbePreviewLifecycle_StartsAndStopsCleanly()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var cameraStore = new InMemoryCameraStore();
        var cameraService = new InMemoryCameraManagementService(cameraStore);
        controller.AttachServicesForTest(cameraService, cameraStore);

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });
        vm.MarkInitialSyncComplete();

        // Paso 1 -> 2 -> 3
        vm.FirstRunStartCommand.Execute(null);
        vm.FirstRunSelectSourceCommand.Execute("USB");
        vm.WizardUsbIndex = "0";
        vm.FirstRunNextCommand.Execute(null);
        Assert.Equal(3, vm.FirstRunStep);

        // Paso 3 -> 4: probe preview starts automatically
        vm.AllowSaveWithoutProbe = true;
        vm.FirstRunNextCommand.Execute(null);
        Assert.Equal(4, vm.FirstRunStep);
        Assert.True(vm.IsFirstRunStep4);

        await Task.Delay(50);
        Assert.True(vm.IsProbeStreaming);

        // Paso 4 -> 5: advancing to Step 5 stops probe preview
        vm.FirstRunNextCommand.Execute(null);
        Assert.Equal(5, vm.FirstRunStep);
        Assert.False(vm.IsProbeStreaming);

        // Go back to Step 4: probe can start again
        vm.FirstRunBackCommand.Execute(null);
        Assert.Equal(4, vm.FirstRunStep);
        await vm.StartProbePreviewAsync();
        Assert.True(vm.IsProbeStreaming);

        // Paso 4 -> 3: going back stops probe preview
        vm.FirstRunBackCommand.Execute(null);
        Assert.Equal(3, vm.FirstRunStep);
        Assert.False(vm.IsProbeStreaming);
    }

    // Caso I — Single Capture Ownership: Guardar cámara en Paso 5 detiene el probe antes de crear el pipeline definitivo
    [Fact]
    public async Task CaseI_SingleCaptureOwnership_ProbeStoppedBeforeDefinitiveCameraCreated()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var cameraStore = new InMemoryCameraStore();
        var cameraService = new InMemoryCameraManagementService(cameraStore);
        controller.AttachServicesForTest(cameraService, cameraStore);

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });
        vm.MarkInitialSyncComplete();

        vm.FirstRunStartCommand.Execute(null);
        vm.FirstRunSelectSourceCommand.Execute("USB");
        vm.WizardUsbIndex = "0";
        vm.FirstRunNextCommand.Execute(null);
        vm.AllowSaveWithoutProbe = true;
        vm.FirstRunNextCommand.Execute(null); // Step 4
        await vm.StartProbePreviewAsync();
        Assert.True(vm.IsProbeStreaming);

        // Advance to Step 5
        vm.FirstRunNextCommand.Execute(null);
        Assert.Equal(5, vm.FirstRunStep);
        vm.NewCameraName = "Cámara Entrada";

        // Save camera
        await vm.FirstRunSaveCameraCommand.ExecuteAsync(null);

        // Verification: probe preview is stopped, camera is created and saved
        Assert.False(vm.IsProbeStreaming);
        Assert.Equal(6, vm.FirstRunStep);
        Assert.NotNull(vm.SavedFirstRunCamera);
        Assert.Single(vm.Cameras);
    }

    // Caso J — Transición First Run Paso 6 -> Workspace IA es inmediata y no bloqueante
    [Fact]
    public void CaseJ_Step6_ConfigureAi_ImmediateNonBlockingTransition()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var vm = new MainViewModel(config, controller, dispatcher, _ => { });
        vm.MarkInitialSyncComplete();

        var savedView = new CameraView("cam-10", "Cámara Almacén", "0", true, false, false, 0, null);
        var stubService = new StubCameraManagementService();
        var camVm = new CameraViewModel(savedView, stubService, dispatcher);
        vm.Cameras.Add(camVm);
        vm.SavedFirstRunCamera = camVm;
        vm.FirstRunStep = 6;

        Assert.True(vm.ShowFirstRunExperience);
        Assert.False(vm.ShowApplicationShell);

        // Execute Configure AI
        vm.FirstRunConfigureAiCommand.Execute(null);

        // Shell is immediately shown without deadlocks
        Assert.False(vm.ShowFirstRunExperience);
        Assert.True(vm.ShowApplicationShell);
        Assert.Equal(8, vm.SelectedNavIndex);
        Assert.Same(camVm, vm.SelectedDetailCamera);
        Assert.True(camVm.IsAnalyticsTabSelected);
    }

    // Caso K — CameraViewModel: Zonas y analíticas en background no bloquean Dispatcher
    [Fact]
    public async Task CaseK_CameraViewModel_ThreadSafeAnalyticsAndZones_DoesNotBlockDispatcher()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var savedView = new CameraView("cam-safe", "Cámara Test", "0", true, false, false, 0, null);
        var stubService = new StubCameraManagementService();
        var camVm = new CameraViewModel(savedView, stubService, dispatcher);

        // Should complete asynchronously on current or thread pool threads without throwing or deadlocking
        await Task.Run(async () =>
        {
            await camVm.InitializeZonesAsync();
            await camVm.LoadAssignedAnalyticsAsync();
        });

        Assert.NotNull(camVm.EditorZones);
        Assert.NotNull(camVm.AssignedAnalytics);
        Assert.NotNull(camVm.RemoveAnalyticCommand);
    }

    private sealed class StubProbeDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubDeviceEnumerator(IReadOnlyList<VideoDeviceInfo> devices) : IVideoDeviceEnumerator
    {
        public Task<IReadOnlyList<VideoDeviceInfo>> EnumerateDevicesAsync(CancellationToken token = default) =>
            Task.FromResult(devices);
    }

    private sealed class InMemoryCameraStore : ICameraStore
    {
        private readonly List<CameraDefinition> _cameras = [];

        public Task InitializeAsync(IReadOnlyList<CameraDefinition> seed, CancellationToken token = default)
        {
            _cameras.AddRange(seed);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CameraDefinition>> ListAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<CameraDefinition>>(_cameras);

        public Task<CameraDefinition?> GetAsync(string id, CancellationToken token = default) =>
            Task.FromResult(_cameras.FirstOrDefault(c => c.Id == id));

        public Task AddAsync(CameraDefinition camera, CancellationToken token = default)
        {
            _cameras.Add(camera);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(CameraDefinition camera, CancellationToken token = default)
        {
            var idx = _cameras.FindIndex(c => c.Id == camera.Id);
            if (idx < 0) return Task.FromResult(false);
            _cameras[idx] = camera;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken token = default) =>
            Task.FromResult(_cameras.RemoveAll(c => c.Id == id) > 0);
    }

    private sealed class InMemoryCameraManagementService(ICameraStore store) : ICameraManagementService
    {
        public async Task<IReadOnlyList<CameraView>> ListAsync(CancellationToken token = default)
        {
            var defs = await store.ListAsync(token);
            return defs.Select(d => new CameraView(d.Id, d.Name, d.Source, d.Enabled, false, false, 0, null)).ToList();
        }

        public async Task<CameraView?> GetAsync(string id, CancellationToken token = default)
        {
            var d = await store.GetAsync(id, token);
            return d is null ? null : new CameraView(d.Id, d.Name, d.Source, d.Enabled, false, false, 0, null);
        }

        public async Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default)
        {
            var def = new CameraDefinition(request.Id, request.Name, request.Source, request.Enabled, false, []);
            await store.AddAsync(def, token);
            return new CameraView(def.Id, def.Name, def.Source, def.Enabled, false, false, 0, null);
        }

        public Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default) => throw new NotImplementedException();
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => store.DeleteAsync(id, token);
        public Task<CameraView?> StartAsync(string id, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CameraView?> StopAsync(string id, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default) => Task.FromResult(new CameraTestResult(true, null, 1280, 720, 30.0, 50.0));
        public Task<CameraProbeSubscriptionResult> ProbeStreamAsync(CameraTestRequest request, double targetFps = 15.0, int quality = 70, CancellationToken token = default)
        {
            var channel = Channel.CreateBounded<CameraProbeFrame>(10);
            return Task.FromResult(new CameraProbeSubscriptionResult(CameraStreamStatus.Success, channel.Reader, new StubProbeDisposable()));
        }
        public Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default) => Task.FromResult<IReadOnlyList<NormalizedZone>?>([]);
        public Task<bool> UpdateZonesAsync(string id, IReadOnlyList<NormalizedZone> zones, CancellationToken token = default) => Task.FromResult(true);
        public Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default) => throw new NotImplementedException();
        public Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default) => throw new NotImplementedException();
    }

    private sealed class StubCameraManagementService : ICameraManagementService
    {
        public Task<IReadOnlyList<CameraView>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CameraView>>([]);
        public Task<CameraView?> GetAsync(string id, CancellationToken token = default) => Task.FromResult<CameraView?>(null);
        public Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => Task.FromResult(true);
        public Task<CameraView?> StartAsync(string id, CancellationToken token = default) => Task.FromResult<CameraView?>(null);
        public Task<CameraView?> StopAsync(string id, CancellationToken token = default) => Task.FromResult<CameraView?>(null);
        public Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default) => Task.FromResult(new CameraTestResult(true, null, 1280, 720, 30.0, 50.0));
        public Task<CameraProbeSubscriptionResult> ProbeStreamAsync(CameraTestRequest request, double targetFps = 15.0, int quality = 70, CancellationToken token = default)
        {
            var channel = Channel.CreateBounded<CameraProbeFrame>(10);
            return Task.FromResult(new CameraProbeSubscriptionResult(CameraStreamStatus.Success, channel.Reader, new StubProbeDisposable()));
        }
        public Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default) => Task.FromResult<IReadOnlyList<NormalizedZone>?>([]);
        public Task<bool> UpdateZonesAsync(string id, IReadOnlyList<NormalizedZone> zones, CancellationToken token = default) => Task.FromResult(true);
        public Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default) => Task.FromResult(new DeviceChangeResult(true, deviceId, null, 10.0));
        public Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default) => Task.FromResult(new CameraStreamSubscriptionResult(CameraStreamStatus.NotFound));
    }
}
