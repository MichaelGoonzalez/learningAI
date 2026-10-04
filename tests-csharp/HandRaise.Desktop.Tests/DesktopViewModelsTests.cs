using System.Windows.Threading;
using HandRaise.Application.Analytics;
using HandRaise.Desktop.Configuration;
using HandRaise.Desktop.Services;
using HandRaise.Desktop.ViewModels;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using HandRaise.Domain.Notifications;
using HandRaise.Domain.Rules;
using HandRaise.Host.Services;

namespace HandRaise.Desktop.Tests;

public sealed class DesktopViewModelsTests
{
    private static DesktopConfiguration CreateTestConfig() => new(
        Model: new ModelSettings("models/yolo26n-pose.onnx", "yolo26n-pose", 640, 640, 80, 17, 0.25f, 0.45f, 100),
        Capture: new CaptureSettings(30, 1000, 5000, 5000, 5000),
        Hands: new HandSettings(0.5, 0.2, true, 10, 3, 3, 1000, 3000),
        Tracker: new TrackerSettings(0.5f, 0.2f, 0.6f, 0.7f, 0.5f, 30, 30, 1, 1, 1),
        Storage: new StorageSettings("data/events.db", "data/snapshots", 1000, 50, 0.2, 75, 30, 1000, 60),
        Cameras: [],
        Ui: new UiSettings(true, 100));

    [Fact]
    public void MainViewModel_InitializesWithRebrandedTitlesAndDefaultOverview()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        Assert.Equal("VisionControl Edge", vm.AppTitle);
        Assert.Equal("Nodo de Visión Artificial", vm.AppSubtitle);
        Assert.True(vm.IsOverviewSelected);
        Assert.False(vm.IsCamerasSelected);
        Assert.False(vm.IsAlertsSelected);
        Assert.False(vm.IsCameraDetailSelected);
    }

    [Fact]
    public void MainViewModel_NavigationChangesUpdateSelectedState()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        vm.SelectedNavIndex = 1;
        Assert.True(vm.IsCamerasSelected);
        Assert.False(vm.IsOverviewSelected);

        vm.SelectedNavIndex = 2;
        Assert.True(vm.IsAlertsSelected);

        vm.SelectedNavIndex = 3;
        Assert.True(vm.IsIntegrationsSelected);

        vm.SelectedNavIndex = 4;
        Assert.True(vm.IsModelsSelected);

        vm.SelectedNavIndex = 5;
        Assert.True(vm.IsSystemSelected);

        vm.SelectedNavIndex = 6;
        Assert.True(vm.IsWebConnectionSelected);

        vm.SelectedNavIndex = 7;
        Assert.True(vm.IsDiagnosticsSelected);

        vm.BackToCamerasCommand.Execute(null);
        Assert.True(vm.IsCamerasSelected);
    }

    [Fact]
    public async Task AlertItemViewModel_AcknowledgeAndResolveUpdateStatus()
    {
        var alert = new OperationalAlert(
            Id: "alert-101",
            RuleId: "rule-max-occupancy",
            CameraId: "cam-lobby",
            AnalyticInstanceId: "inst-1",
            SourceEventId: "ev-1",
            Severity: AlertSeverity.Critical,
            Status: AlertStatus.Open,
            Title: "Aforo excedido",
            Description: "15 personas detectadas",
            CreatedAt: DateTimeOffset.UtcNow);

        var acked = false;
        var resolved = false;

        var vm = new AlertItemViewModel(
            alert,
            onAcknowledge: id => { acked = id == "alert-101"; return Task.CompletedTask; },
            onResolve: id => { resolved = id == "alert-101"; return Task.CompletedTask; });

        Assert.Equal("CRÍTICA", vm.SeverityText);
        Assert.Equal("Abierta", vm.StatusText);
        Assert.True(vm.CanAcknowledge);
        Assert.True(vm.CanResolve);

        await vm.AcknowledgeCommand.ExecuteAsync(null);
        Assert.True(acked);
        Assert.Equal(AlertStatus.Acknowledged, vm.Status);
        Assert.Equal("Reconocida", vm.StatusText);
        Assert.False(vm.CanAcknowledge);
        Assert.True(vm.CanResolve);

        await vm.ResolveCommand.ExecuteAsync(null);
        Assert.True(resolved);
        Assert.Equal(AlertStatus.Resolved, vm.Status);
        Assert.Equal("Resuelta", vm.StatusText);
        Assert.False(vm.CanAcknowledge);
        Assert.False(vm.CanResolve);
    }

    [Fact]
    public void ModelItemViewModel_MapsDescriptorCorrectly()
    {
        var desc = new ModelDescriptor(
            Id: "yolo26n-pose",
            DisplayName: "YOLO26 Nano Pose",
            Version: "1.0.0",
            Format: ModelFormat.Onnx,
            Capabilities: [InferenceCapability.PoseEstimation, InferenceCapability.ObjectDetection],
            InputWidth: 640,
            InputHeight: 640,
            Path: "models/yolo26n-pose.onnx",
            ClassCount: 80,
            KeypointCount: 17,
            ConfidenceThreshold: 0.25f,
            IouThreshold: 0.45f,
            MaximumDetections: 100,
            PreferredDevice: "gpu",
            MemoryEstimateMb: 150.0);

        var vm = new ModelItemViewModel(desc);

        Assert.Equal("yolo26n-pose", vm.Id);
        Assert.Equal("YOLO26 Nano Pose", vm.DisplayName);
        Assert.Equal("1.0.0", vm.Version);
        Assert.Equal("ONNX", vm.Format);
        Assert.Contains("PoseEstimation", vm.CapabilitiesSummary);
        Assert.Equal("640x640", vm.Resolution);
        Assert.Equal("150 MB", vm.MemoryMb);
    }

    [Fact]
    public void IntegrationViewModels_MapDestinationsAndPoliciesCorrectly()
    {
        var dest = new NotificationDestination(
            Id: "dest-webhook",
            Name: "Slack Alerts Webhook",
            Type: NotificationDestinationType.Webhook,
            Configuration: new Dictionary<string, object?> { ["url"] = "https://hooks.slack.com/services/xyz" },
            Enabled: true);

        var destVm = new DestinationItemViewModel(dest);
        Assert.Equal("dest-webhook", destVm.Id);
        Assert.Equal("Slack Alerts Webhook", destVm.Name);
        Assert.Equal("Webhook HTTP", destVm.TypeText);
        Assert.Equal("Activo", destVm.StatusText);

        var policy = new NotificationPolicy(
            Id: "pol-critical",
            Name: "Notificar Críticas",
            SeverityFilter: [AlertSeverity.Critical],
            DestinationId: "dest-webhook",
            Enabled: true);

        var polVm = new PolicyItemViewModel(policy);
        Assert.Equal("pol-critical", polVm.Id);
        Assert.Contains("Critical", polVm.MinSeverity);
        Assert.Equal("dest-webhook", polVm.DestinationId);
        Assert.Equal("Activa", polVm.StatusText);
    }

    [Fact]
    public void MainViewModel_FiltersAlertsBySeverityAndStatus()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        var a1 = new OperationalAlert("a1", "r1", "c1", "i1", "e1", AlertSeverity.Critical, AlertStatus.Open, "Msg 1", "", DateTimeOffset.UtcNow);
        var a2 = new OperationalAlert("a2", "r2", "c1", "i1", "e2", AlertSeverity.Low, AlertStatus.Resolved, "Msg 2", "", DateTimeOffset.UtcNow);

        vm.Alerts.Add(new AlertItemViewModel(a1));
        vm.Alerts.Add(new AlertItemViewModel(a2));

        Assert.Equal(2, vm.FilteredAlerts.Count());

        vm.SelectedAlertSeverityFilter = "Critical";
        Assert.Single(vm.FilteredAlerts);
        Assert.Equal("a1", vm.FilteredAlerts.First().Id);

        vm.SelectedAlertSeverityFilter = "Todas";
        vm.SelectedAlertStatusFilter = "Resuelta";
        Assert.Single(vm.FilteredAlerts);
        Assert.Equal("a2", vm.FilteredAlerts.First().Id);
    }

    [Fact]
    public void MainViewModel_EmptyStates_ReflectCollectionSizes()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        Assert.True(vm.HasNoCameras);
        Assert.False(vm.HasCameras);

        Assert.True(vm.HasNoAlerts);
        Assert.False(vm.HasAlerts);

        Assert.True(vm.HasNoDestinations);
        Assert.False(vm.HasDestinations);

        Assert.True(vm.HasNoPolicies);
        Assert.False(vm.HasPolicies);

        Assert.True(vm.HasNoAttempts);
        Assert.False(vm.HasAttempts);

        Assert.True(vm.HasNoModels);
        Assert.False(vm.HasModels);
    }

    [Fact]
    public void CameraViewModel_EmptyStates_ReflectAssignedAnalyticsAndZones()
    {
        var cameraView = new CameraView("cam-test", "Cámara Test", "0", true, false, false, 0.0, null);
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new CameraViewModel(cameraView, new StubCameraManagementService(), dispatcher);

        Assert.True(vm.HasNoAssignedAnalytics);
        Assert.False(vm.HasAssignedAnalytics);

        Assert.True(vm.HasNoZones);
        Assert.False(vm.HasZones);

        Assert.True(vm.HasNoLines);
        Assert.False(vm.HasLines);
    }

    [Fact]
    public async Task CameraAnalyticItemViewModel_ToggleAndRemove_TriggersCallbacks()
    {
        var instance = new CameraAnalyticInstance("inst-1", "cam-1", "hand_raise", "Levantamiento de Mano", true, AnalyticStatus.Active);
        var definition = new AnalyticDefinition("hand_raise", "Levantamiento de Mano", "Detecta gestos", AnalyticCategory.Operations, "1.0.0", [InferenceCapability.PoseEstimation], [AnalyticFeature.Tracking], ["hand_raise_started"], []);

        var toggled = false;
        var removed = false;

        var vm = new CameraAnalyticItemViewModel(
            instance,
            definition,
            onToggle: (id, enabled) => { toggled = true; return Task.CompletedTask; },
            onRemove: id => { removed = true; return Task.CompletedTask; });

        Assert.Equal("Levantamiento de Mano", vm.DisplayName);
        Assert.Equal("Activa", vm.StatusText);
        Assert.True(vm.Enabled);

        await vm.ToggleEnabledCommand.ExecuteAsync(null);
        Assert.True(toggled);
        Assert.False(vm.Enabled);
        Assert.Equal("Pausada", vm.StatusText);

        await vm.RemoveCommand.ExecuteAsync(null);
        Assert.True(removed);
    }

    [Fact]
    public void AttemptItemViewModel_MapsSuccessAndFailureCorrectly()
    {
        var okAttempt = new NotificationAttempt(
            Id: "att-1",
            AlertId: "al-1",
            PolicyId: "pol-1",
            DestinationId: "dest-slack",
            AttemptNumber: 1,
            Status: NotificationStatus.Succeeded,
            TimestampUtc: DateTimeOffset.UtcNow,
            ResponseCode: 200,
            ErrorSanitized: null);

        var okVm = new AttemptItemViewModel(okAttempt);
        Assert.True(okVm.Success);
        Assert.Equal("Enviado (OK)", okVm.StatusText);
        Assert.Null(okVm.Error);

        var failAttempt = new NotificationAttempt(
            Id: "att-2",
            AlertId: "al-1",
            PolicyId: "pol-1",
            DestinationId: "dest-mqtt",
            AttemptNumber: 1,
            Status: NotificationStatus.Failed,
            TimestampUtc: DateTimeOffset.UtcNow,
            ResponseCode: 500,
            ErrorSanitized: "Connection timed out");

        var failVm = new AttemptItemViewModel(failAttempt);
        Assert.False(failVm.Success);
        Assert.Equal("Error", failVm.StatusText);
        Assert.Equal("Connection timed out", failVm.Error);
    }

    [Fact]
    public void MainWindow_CanBeConstructedAndBound_WithoutBindingExceptions()
    {
        RunInSta(() =>
        {
            var config = CreateTestConfig();
            var controller = new NodeHostController();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var vm = new MainViewModel(config, controller, dispatcher, _ => { });

            var window = new MainWindow(vm);
            Assert.NotNull(window);
            Assert.Same(vm, window.DataContext);
            Assert.True(vm.IsOverviewSelected);
        });
    }

    [Fact]
    public void MainWindow_NavigationThroughAllTabs_DoesNotThrowBindingExceptions()
    {
        RunInSta(() =>
        {
            var config = CreateTestConfig();
            var controller = new NodeHostController();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var vm = new MainViewModel(config, controller, dispatcher, _ => { });

            var window = new MainWindow(vm);

            for (var i = 0; i <= 7; i++)
            {
                vm.NavigateCommand.Execute(i);
                Assert.Equal(i, vm.SelectedNavIndex);

                vm.NavigateCommand.Execute(i.ToString());
                Assert.Equal(i, vm.SelectedNavIndex);
            }

            Assert.True(vm.IsDiagnosticsSelected);
            vm.BackToCamerasCommand.Execute(null);
            Assert.True(vm.IsCamerasSelected);
        });
    }

    [Fact]
    public async Task MainViewModel_AddCameraWizard_EnumeratesUsbDevicesAndSelectsDefault()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var fakeEnumerator = new StubVideoDeviceEnumerator([
            new HandRaise.Application.Capture.VideoDeviceInfo(0, "Logitech C920 HD Pro", "USB"),
            new HandRaise.Application.Capture.VideoDeviceInfo(1, "Integrated Camera", "USB")
        ]);

        var vm = new MainViewModel(config, controller, dispatcher, _ => { }, videoDeviceEnumerator: fakeEnumerator);

        vm.ShowAddCameraModalCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(vm.IsAddCameraModalOpen);
        Assert.Equal(2, vm.DetectedUsbDevices.Count);
        Assert.NotNull(vm.SelectedUsbDevice);
        Assert.Equal("Logitech C920 HD Pro", vm.SelectedUsbDevice.Name);
        Assert.Equal("0", vm.WizardUsbIndex);

        vm.SelectedUsbDevice = vm.DetectedUsbDevices[1];
        Assert.Equal("1", vm.WizardUsbIndex);
    }

    [Fact]
    public void CameraViewModel_AiSolutionWizard_ProgressionAndFeatureDetection()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();
        var catalog = new StubAnalyticCatalog([
            new AnalyticDefinition(
                Id: "zone_intrusion",
                DisplayName: "Intrusión en Zona",
                Description: "Detecta intrusión",
                Category: AnalyticCategory.Security,
                Version: "1.0.0",
                RequiredCapabilities: [InferenceCapability.PoseEstimation],
                Features: [AnalyticFeature.Zones, AnalyticFeature.Sensitivity],
                ProducedEventTypes: ["zone_intrusion"],
                Parameters: []),
            new AnalyticDefinition(
                Id: "hand_raise",
                DisplayName: "Mano Levantada",
                Description: "Detecta mano",
                Category: AnalyticCategory.General,
                Version: "1.0.0",
                RequiredCapabilities: [InferenceCapability.PoseEstimation],
                Features: [AnalyticFeature.Snapshots, AnalyticFeature.Sensitivity],
                ProducedEventTypes: ["hand_raise"],
                Parameters: [])
        ]);

        var vm = new CameraViewModel(
            new CameraView("cam-1", "Test Camera", "0", true, true, true, 30.0, null),
            camService,
            dispatcher,
            analyticCatalog: catalog);

        vm.OpenAddAiModalCommand.Execute(null);

        Assert.True(vm.IsAddAiModalOpen);
        Assert.Equal(1, vm.AiWizardStep);
        Assert.True(vm.IsAiWizardStep1);

        // Step 1: Select zone_intrusion
        vm.SelectedCatalogAnalytic = catalog.GetAll()[0];
        Assert.True(vm.RequiresZone);
        Assert.False(vm.RequiresLine);
        Assert.False(vm.HasEvidenceFeature);

        vm.NextAiWizardStepCommand.Execute(null);
        Assert.Equal(2, vm.AiWizardStep);
        Assert.True(vm.IsAiWizardStep2);

        vm.NextAiWizardStepCommand.Execute(null);
        Assert.Equal(3, vm.AiWizardStep);
        Assert.True(vm.IsAiWizardStep3);

        vm.NextAiWizardStepCommand.Execute(null);
        Assert.Equal(4, vm.AiWizardStep);
        Assert.True(vm.IsAiWizardStep4);

        vm.PrevAiWizardStepCommand.Execute(null);
        Assert.Equal(3, vm.AiWizardStep);

        vm.CloseAddAiModalCommand.Execute(null);
        Assert.False(vm.IsAddAiModalOpen);
    }

    [Fact]
    public async Task CameraViewModel_AiSolutionWizard_ConfirmAndContextualTransitions()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var catalog = new StubAnalyticCatalog([
            new AnalyticDefinition(
                Id: "hand_raise",
                DisplayName: "Detección de Mano Levantada",
                Description: "Reconoce gestos de mano",
                Category: AnalyticCategory.General,
                Version: "1.0.0",
                RequiredCapabilities: [InferenceCapability.PoseEstimation],
                Features: [AnalyticFeature.Snapshots, AnalyticFeature.Sensitivity],
                ProducedEventTypes: ["hand_raise"],
                Parameters: [])
        ]);

        var vm = new CameraViewModel(
            new CameraView("cam-1", "Camara 1", "0", true, true, true, 30.0, null),
            camService,
            dispatcher,
            analyticService: analyticService,
            analyticCatalog: catalog);

        vm.OpenAddAiModalCommand.Execute(null);
        vm.SelectedCatalogAnalytic = catalog.GetAll()[0];
        vm.AiCustomName = "Mano en Aula 1";
        vm.AiSensitivity = 0.8;

        await vm.ConfirmAddAiSolutionCommand.ExecuteAsync(null);

        Assert.False(vm.IsAddAiModalOpen);
        Assert.True(vm.AiSuccessBannerVisible);
        Assert.Contains("Mano en Aula 1", vm.AiSuccessMessage);

        // Contextual action: Go to Rules
        vm.GoToRulesCommand.Execute(null);
        Assert.True(vm.IsRulesTabSelected);
        Assert.False(vm.AiSuccessBannerVisible);

        // Contextual action: Go to Monitor
        vm.GoToMonitorCommand.Execute(null);
        Assert.True(vm.IsMonitorTabSelected);
    }

    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current == null)
                {
                    var app = new HandRaise.Desktop.App();
                    app.InitializeComponent();
                }
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private sealed class StubVideoDeviceEnumerator(IReadOnlyList<HandRaise.Application.Capture.VideoDeviceInfo> devices) : HandRaise.Application.Capture.IVideoDeviceEnumerator
    {
        public Task<IReadOnlyList<HandRaise.Application.Capture.VideoDeviceInfo>> EnumerateDevicesAsync(CancellationToken token = default) =>
            Task.FromResult(devices);
    }

    private sealed class StubAnalyticCatalog(IReadOnlyList<AnalyticDefinition> definitions) : IAnalyticCatalog
    {
        public IReadOnlyList<AnalyticDefinition> GetAll() => definitions;
        public AnalyticDefinition? GetById(string id) => definitions.FirstOrDefault(d => d.Id == id);
        public (bool IsValid, string? ErrorMessage) ValidateConfiguration(string analyticTypeId, IReadOnlyDictionary<string, object?>? configuration) => (true, null);
    }

    private sealed class StubAnalyticManagementService : IAnalyticManagementService
    {
        private readonly List<CameraAnalyticInstance> _instances = [];

        public IReadOnlyList<AnalyticDefinition> GetCatalog() => [];

        public Task<IReadOnlyList<CameraAnalyticInstance>?> ListByCameraAsync(string cameraId, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<CameraAnalyticInstance>?>(_instances.Where(i => i.CameraId == cameraId).ToList());

        public Task<CameraAnalyticInstance?> GetAsync(string cameraId, string instanceId, CancellationToken token = default) =>
            Task.FromResult(_instances.FirstOrDefault(i => i.CameraId == cameraId && i.Id == instanceId));

        public Task<CameraAnalyticInstance> CreateAsync(string cameraId, CameraAnalyticWriteRequest request, CancellationToken token = default)
        {
            var inst = new CameraAnalyticInstance(
                Id: request.Id ?? Guid.NewGuid().ToString("N")[..8],
                CameraId: cameraId,
                AnalyticTypeId: request.AnalyticTypeId ?? "hand_raise",
                Name: request.Name ?? "Analytic",
                Enabled: request.Enabled ?? true,
                Status: AnalyticStatus.Active,
                Configuration: request.Configuration,
                AssignedZoneIds: request.AssignedZoneIds,
                AssignedLineIds: request.AssignedLineIds,
                CreatedAt: DateTimeOffset.UtcNow);
            _instances.Add(inst);
            return Task.FromResult(inst);
        }

        public Task<CameraAnalyticInstance?> UpdateAsync(string cameraId, string instanceId, CameraAnalyticWriteRequest request, CancellationToken token = default)
        {
            var existing = _instances.FirstOrDefault(i => i.CameraId == cameraId && i.Id == instanceId);
            if (existing == null) return Task.FromResult<CameraAnalyticInstance?>(null);
            _instances.Remove(existing);
            var updated = existing with { Enabled = request.Enabled ?? existing.Enabled };
            _instances.Add(updated);
            return Task.FromResult<CameraAnalyticInstance?>(updated);
        }

        public Task<bool> DeleteAsync(string cameraId, string instanceId, CancellationToken token = default)
        {
            var removed = _instances.RemoveAll(i => i.CameraId == cameraId && i.Id == instanceId) > 0;
            return Task.FromResult(removed);
        }
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
        public Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<HandRaise.Application.Zones.NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default) => Task.FromResult<IReadOnlyList<HandRaise.Application.Zones.NormalizedZone>?>([]);
        public Task<bool> UpdateZonesAsync(string id, IReadOnlyList<HandRaise.Application.Zones.NormalizedZone> zones, CancellationToken token = default) => Task.FromResult(true);
        public Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default) => Task.FromResult(new DeviceChangeResult(true, deviceId, null, 10.0));
        public Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default) => Task.FromResult(new CameraStreamSubscriptionResult(CameraStreamStatus.NotFound));
    }
}
