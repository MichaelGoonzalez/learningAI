using System.Windows.Threading;
using HandRaise.Application.Analytics;
using HandRaise.Application.Events;
using HandRaise.Application.Rules;
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
        Assert.Equal("Sistema de Visión Artificial", vm.AppSubtitle);
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
        Assert.True(vm.IsSettingsOrTechnicalSelected);
        Assert.Equal(3, vm.SettingsTabIndex);

        vm.SelectedNavIndex = 5;
        Assert.True(vm.IsSystemSelected);
        Assert.True(vm.IsSettingsOrTechnicalSelected);
        Assert.Equal(0, vm.SettingsTabIndex);

        vm.SelectedNavIndex = 6;
        Assert.True(vm.IsWebConnectionSelected);
        Assert.True(vm.IsSettingsOrTechnicalSelected);
        Assert.Equal(2, vm.SettingsTabIndex);

        vm.SelectedNavIndex = 9;
        Assert.True(vm.IsPerformanceSettingsSelected);
        Assert.True(vm.IsSettingsOrTechnicalSelected);
        Assert.Equal(1, vm.SettingsTabIndex);

        vm.SelectedNavIndex = 7;
        Assert.True(vm.IsDiagnosticsSelected);
        Assert.False(vm.IsSettingsOrTechnicalSelected);

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
        Assert.True(vm.IsLoadingInitialState);
        Assert.False(vm.HasNoCameras);
        vm.MarkInitialSyncComplete();

        Assert.False(vm.IsLoadingInitialState);
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
    public async Task MainViewModel_AddCameraWizard_EnumeratesUsbDevicesWithoutAutoSelecting()
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
        Assert.Null(vm.SelectedUsbDevice);
        Assert.True(string.IsNullOrEmpty(vm.WizardUsbIndex));

        // Conscious user selection
        vm.SelectedUsbDevice = vm.DetectedUsbDevices[0];
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

    [Fact]
    public void MainViewModel_InitializesWithZeroCamerasAndCleanVersion()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });
        vm.MarkInitialSyncComplete();

        Assert.True(vm.HasNoCameras);
        Assert.False(vm.HasCameras);
        Assert.Equal(0, vm.TotalCamerasCount);
        Assert.Equal("VisionControl Edge · v0.9.0", vm.AboutText);
        Assert.Equal("Sistema de Visión Artificial", vm.AppSubtitle);
    }

    [Fact]
    public void MainViewModel_CategoryNavigation_WithZeroCameras_NavigatesToCamerasWithHint()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        vm.NavigateToAnalyticsCommand.Execute(null);
        Assert.True(vm.IsCamerasSelected);
        Assert.Contains("Agregue una cámara", vm.StatusMessage);

        vm.NavigateToRulesCommand.Execute(null);
        Assert.True(vm.IsCamerasSelected);
        Assert.Contains("Agregue una cámara", vm.StatusMessage);

        vm.NavigateToEventsCommand.Execute(null);
        Assert.True(vm.IsCamerasSelected);
        Assert.Contains("Agregue una cámara", vm.StatusMessage);
    }

    [Fact]
    public void MainViewModel_OpenAddCameraWizard_DoesNotAutoSelectUsbIndexZero()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        vm.ShowAddCameraModalCommand.Execute(null);

        Assert.True(vm.IsAddCameraModalOpen);
        Assert.Null(vm.SelectedUsbDevice);
        Assert.True(string.IsNullOrEmpty(vm.WizardUsbIndex));
    }

    [Fact]
    public void MainViewModel_PostSaveBanner_CanBeDismissed()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        vm.ShowPostSaveBanner = true;
        Assert.True(vm.ShowPostSaveBanner);

        vm.DismissPostSaveBannerCommand.Execute(null);
        Assert.False(vm.ShowPostSaveBanner);
    }

    [Fact]
    public async Task CameraViewModel_VirginCamera_DefaultsToZeroAnalyticsAndNoInferenceSummary()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();

        var vm = new CameraViewModel(
            new CameraView("cam-virgin", "Cámara Limpia", "0", true, true, true, 30.0, null),
            camService,
            dispatcher,
            analyticService: analyticService);

        await vm.LoadAssignedAnalyticsAsync();

        Assert.True(vm.HasNoConfiguredAnalytics);
        Assert.False(vm.HasConfiguredAnalytics);
        Assert.Empty(vm.AssignedAnalytics);
        Assert.Equal(0, vm.ActiveAnalyticsCount);
        Assert.True(vm.IsNormalMode);
        Assert.False(vm.IsCreatingGeometryForAi);
        Assert.True(vm.OpenAddAiModalCommand.CanExecute(null));
        Assert.True(vm.StartDirectZoneCreationCommand.CanExecute(null));
        Assert.True(vm.StartDirectLineCreationCommand.CanExecute(null));
        Assert.True(vm.SaveConfigCommand.CanExecute(null));
        Assert.Contains("Sin análisis activo", vm.ActivitySummaryText);
        Assert.Equal("⚪ Sin análisis activo", vm.OperationalResultsSummary);
    }

    [Fact]
    public void BooleanToVisibilityConverter_SupportsInverseParameter()
    {
        var conv = new HandRaise.Desktop.Converters.BooleanToVisibilityConverter();
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Equal(System.Windows.Visibility.Visible, conv.Convert(true, typeof(System.Windows.Visibility), null, culture));
        Assert.Equal(System.Windows.Visibility.Collapsed, conv.Convert(false, typeof(System.Windows.Visibility), null, culture));
        Assert.Equal(System.Windows.Visibility.Visible, conv.Convert(false, typeof(System.Windows.Visibility), "inverse", culture));
        Assert.Equal(System.Windows.Visibility.Collapsed, conv.Convert(true, typeof(System.Windows.Visibility), "inverse", culture));
    }

    [Fact]
    public void CameraViewModel_DirectGeometryCreation_ManagesDrawingModesAndReturnsToNormal()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();

        var vm = new CameraViewModel(
            new CameraView("cam-geom", "Cámara Zonas", "0", true, true, true, 30.0, null),
            camService,
            dispatcher);

        Assert.True(vm.IsNormalMode);
        Assert.False(vm.IsCreatingGeometryForAi);
        Assert.False(vm.IsCreatingContextualZone);
        Assert.False(vm.IsCreatingContextualLine);

        // Direct Zone creation
        vm.StartDirectZoneCreationCommand.Execute(null);
        Assert.True(vm.IsCreatingContextualZone);
        Assert.True(vm.IsCreatingGeometryForAi);
        Assert.False(vm.IsNormalMode);
        Assert.False(vm.IsCreatingContextualLine);

        vm.CancelContextualGeometryAndReturnCommand.Execute(null);
        Assert.False(vm.IsCreatingContextualZone);
        Assert.False(vm.IsCreatingGeometryForAi);
        Assert.True(vm.IsNormalMode);

        // Direct Line creation
        vm.StartDirectLineCreationCommand.Execute(null);
        Assert.True(vm.IsCreatingContextualLine);
        Assert.True(vm.IsCreatingGeometryForAi);
        Assert.False(vm.IsNormalMode);
        Assert.False(vm.IsCreatingContextualZone);

        vm.CancelContextualGeometryAndReturnCommand.Execute(null);
        Assert.False(vm.IsCreatingContextualLine);
        Assert.False(vm.IsCreatingGeometryForAi);
        Assert.True(vm.IsNormalMode);
    }

    [Fact]
    public void CameraViewModel_SelectCatalogAnalyticAndNextCommand_AdvancesToStep2()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();
        var catalogDef = new AnalyticDefinition(
            Id: "person_detect",
            DisplayName: "Detección de Personas",
            Description: "Detecta siluetas humanas",
            Category: AnalyticCategory.Security,
            Version: "1.0.0",
            RequiredCapabilities: [InferenceCapability.PoseEstimation],
            Features: [AnalyticFeature.Zones],
            ProducedEventTypes: ["person_detected"],
            Parameters: []);
        var catalog = new StubAnalyticCatalog([catalogDef]);

        var vm = new CameraViewModel(
            new CameraView("cam-cat", "Cámara Catálogo", "0", true, true, true, 30.0, null),
            camService,
            dispatcher,
            analyticCatalog: catalog);

        vm.OpenAddAiModalCommand.Execute(null);
        Assert.True(vm.IsAddAiModalOpen);
        Assert.True(vm.IsAiWizardStep1);

        // Can select with string ID
        vm.SelectCatalogAnalyticAndNextCommand.Execute("person_detect");

        Assert.NotNull(vm.SelectedCatalogAnalytic);
        Assert.Equal("person_detect", vm.SelectedCatalogAnalytic.Id);
        Assert.True(vm.IsAiWizardStep2);
        Assert.Equal(2, vm.AiWizardStep);
    }

    [Fact]
    public async Task CameraViewModel_AddMultipleAnalytics_UpdatesOperationalSummaryCorrectly()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var def1 = new AnalyticDefinition("person_detect", "Personas", "Desc", AnalyticCategory.Security, "1.0", [InferenceCapability.PoseEstimation], [], [], []);
        var def2 = new AnalyticDefinition("hand_raise", "Manos", "Desc", AnalyticCategory.General, "1.0", [InferenceCapability.PoseEstimation], [], [], []);
        var catalog = new StubAnalyticCatalog([def1, def2]);

        await analyticService.CreateAsync("cam-multi", new CameraAnalyticWriteRequest(AnalyticTypeId: "person_detect", Name: "Personas Entrada", Enabled: true));
        await analyticService.CreateAsync("cam-multi", new CameraAnalyticWriteRequest(AnalyticTypeId: "hand_raise", Name: "Manos Fila", Enabled: true));

        var vm = new CameraViewModel(
            new CameraView("cam-multi", "Cámara Multi", "0", true, true, true, 30.0, null),
            camService,
            dispatcher,
            analyticService: analyticService,
            analyticCatalog: catalog);

        await vm.LoadAssignedAnalyticsAsync();

        Assert.True(vm.HasConfiguredAnalytics);
        Assert.False(vm.HasNoConfiguredAnalytics);
        Assert.Equal(2, vm.AssignedAnalytics.Count);
        Assert.Equal(2, vm.ActiveAnalyticsCount);
        Assert.Contains("2 soluciones IA en ejecución", vm.ActivitySummaryText);
        Assert.Equal("🟢 2 soluciones activas", vm.OperationalResultsSummary);
    }

    [Fact]
    public async Task CameraViewModel_AddHandRaise_UsesDataDrivenParameters_WithoutSensitivity_AndNoZoneByDefault()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var catalog = new StandardAnalyticCatalog();

        var vm = new CameraViewModel(
            new CameraView("cam-hr", "Cámara HR", "0", true, true, true, 30.0, null),
            camService,
            dispatcher,
            analyticService: analyticService,
            analyticCatalog: catalog);

        vm.OpenAddAiModalCommand.Execute(null);
        vm.SelectCatalogAnalyticAndNextCommand.Execute("hand_raise");

        Assert.NotNull(vm.SelectedCatalogAnalytic);
        Assert.Equal("hand_raise", vm.SelectedCatalogAnalytic.Id);
        Assert.False(vm.RequiresZone);
        Assert.True(vm.AllowsOptionalZone);
        Assert.True(vm.IsFullImageAnalysis);
        Assert.False(vm.IsSpecificAreaAnalysis);

        // Verify dynamic parameters loaded from catalog definition
        Assert.Equal(3, vm.DynamicParameters.Count);
        Assert.Contains(vm.DynamicParameters, p => p.Key == "strict_mode" && p.IsBoolean);
        Assert.Contains(vm.DynamicParameters, p => p.Key == "consecutive_frames" && p.IsNumber);
        Assert.Contains(vm.DynamicParameters, p => p.Key == "cooldown_ms" && p.IsNumber);
        Assert.DoesNotContain(vm.DynamicParameters, p => p.Key == "sensitivity");

        await vm.ConfirmAddAiSolutionCommand.ExecuteAsync(null);

        Assert.False(vm.IsAddAiModalOpen);
        Assert.Null(vm.AddAiError);
        Assert.True(vm.AiSuccessBannerVisible);

        var instances = await analyticService.ListByCameraAsync("cam-hr");
        Assert.NotNull(instances);
        var created = Assert.Single(instances);
        Assert.Equal("hand_raise", created.AnalyticTypeId);
        Assert.Null(created.AssignedZoneIds);
        Assert.NotNull(created.Configuration);
        Assert.False(created.Configuration.ContainsKey("sensitivity"));
        Assert.True(created.Configuration.ContainsKey("strict_mode"));
        Assert.True(created.Configuration.ContainsKey("consecutive_frames"));
        Assert.True(created.Configuration.ContainsKey("cooldown_ms"));
    }

    [Fact]
    public void CameraViewModel_RectangleDrawing_ProducesFourClockwiseVertices()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();

        var vm = new CameraViewModel(
            new CameraView("cam-rect", "Cámara Rect", "0", true, true, true, 30.0, null),
            camService,
            dispatcher);

        vm.StartDirectRectangleCreationCommand.Execute(null);

        Assert.True(vm.IsEditingZones);
        Assert.True(vm.IsDrawingRectangle);
        Assert.Empty(vm.DraftPoints);

        // First click (corner 1)
        vm.AddZonePoint(new HandRaise.Application.Zones.NormalizedPoint(0.2, 0.3));
        Assert.Single(vm.DraftPoints);

        // Second click (opposite corner 2)
        vm.AddZonePoint(new HandRaise.Application.Zones.NormalizedPoint(0.8, 0.7));
        Assert.Equal(4, vm.DraftPoints.Count);

        // Check clockwise normalized vertices: (minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY)
        Assert.Equal(0.2, vm.DraftPoints[0].X, 3);
        Assert.Equal(0.3, vm.DraftPoints[0].Y, 3);
        Assert.Equal(0.8, vm.DraftPoints[1].X, 3);
        Assert.Equal(0.3, vm.DraftPoints[1].Y, 3);
        Assert.Equal(0.8, vm.DraftPoints[2].X, 3);
        Assert.Equal(0.7, vm.DraftPoints[2].Y, 3);
        Assert.Equal(0.2, vm.DraftPoints[3].X, 3);
        Assert.Equal(0.7, vm.DraftPoints[3].Y, 3);
    }

    [Fact]
    public void CameraViewModel_ZoneAndLineRequirements_StrictlyFollowAnalyticTypes()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();
        var catalog = new StandardAnalyticCatalog();

        var vm = new CameraViewModel(
            new CameraView("cam-reqs", "Cámara Reqs", "0", true, true, true, 30.0, null),
            camService,
            dispatcher,
            analyticCatalog: catalog);

        // Zone Intrusion: zone required
        vm.SelectedCatalogAnalytic = catalog.GetById("zone_intrusion");
        Assert.True(vm.RequiresZone);
        Assert.False(vm.AllowsOptionalZone);
        Assert.False(vm.RequiresLine);
        Assert.True(vm.IsSpecificAreaAnalysis);
        Assert.False(vm.IsFullImageAnalysis);

        // Hand Raise: zone optional
        vm.SelectedCatalogAnalytic = catalog.GetById("hand_raise");
        Assert.False(vm.RequiresZone);
        Assert.True(vm.AllowsOptionalZone);
        Assert.False(vm.RequiresLine);
        Assert.True(vm.IsFullImageAnalysis);
        Assert.False(vm.IsSpecificAreaAnalysis);

        // Line Crossing: line required
        vm.SelectedCatalogAnalytic = catalog.GetById("line_crossing");
        Assert.False(vm.RequiresZone);
        Assert.False(vm.AllowsOptionalZone);
        Assert.True(vm.RequiresLine);

        // Person Counting: line required
        vm.SelectedCatalogAnalytic = catalog.GetById("person_counting");
        Assert.False(vm.RequiresZone);
        Assert.False(vm.AllowsOptionalZone);
    }

    [Fact]
    public void CameraViewModel_RulesModal_WhenNoAnalytics_DoesNotOpen()
    {
        var camera = new CameraView("cam-1", "Front Cam", "0", true, true, true, 30.0, null);
        var cameraService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var catalog = new StandardAnalyticCatalog();
        var ruleStore = new StubRuleStore();
        var dispatcher = Dispatcher.CurrentDispatcher;

        var vm = new CameraViewModel(
            camera,
            cameraService,
            dispatcher,
            analyticService: analyticService,
            analyticCatalog: catalog,
            ruleStore: ruleStore);

        Assert.True(vm.HasNoConfiguredAnalytics);
        vm.OpenRulesModalCommand.Execute(null);
        Assert.False(vm.IsRulesModalOpen);
    }

    [Fact]
    public async Task CameraViewModel_RulesModal_OpensWithPreselectedAnalytic_AndDataDrivenEventTypes()
    {
        var camera = new CameraView("cam-1", "Front Cam", "0", true, true, true, 30.0, null);
        var cameraService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var catalog = new StandardAnalyticCatalog();
        var ruleStore = new StubRuleStore();
        var dispatcher = Dispatcher.CurrentDispatcher;

        await analyticService.CreateAsync("cam-1", new CameraAnalyticWriteRequest(
            Id: "inst-hr",
            AnalyticTypeId: "hand_raise",
            Name: "Mano Levantada",
            Enabled: true));

        var vm = new CameraViewModel(
            camera,
            cameraService,
            dispatcher,
            analyticService: analyticService,
            analyticCatalog: catalog,
            ruleStore: ruleStore);

        await vm.LoadAssignedAnalyticsAsync();
        Assert.True(vm.HasConfiguredAnalytics);
        Assert.True(vm.HasSingleAssignedAnalytic);
        Assert.False(vm.HasMultipleAssignedAnalytics);

        vm.OpenRulesModalCommand.Execute(null);
        Assert.True(vm.IsRulesModalOpen);
        Assert.NotNull(vm.SelectedRuleAnalytic);
        Assert.Equal("inst-hr", vm.SelectedRuleAnalytic.InstanceId);
        Assert.Equal(["hand_raised", "hand_lowered"], vm.AvailableRuleEventTypes);
    }

    [Fact]
    public async Task CameraViewModel_RulesModal_CreateAndSaveRule_PersistsToRuleStoreWithCorrectProperties()
    {
        var camera = new CameraView("cam-1", "Front Cam", "0", true, true, true, 30.0, null);
        var cameraService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var catalog = new StandardAnalyticCatalog();
        var ruleStore = new StubRuleStore();
        var dispatcher = Dispatcher.CurrentDispatcher;

        await analyticService.CreateAsync("cam-1", new CameraAnalyticWriteRequest(
            Id: "inst-hr",
            AnalyticTypeId: "hand_raise",
            Name: "Mano Levantada",
            Enabled: true));

        var vm = new CameraViewModel(
            camera,
            cameraService,
            dispatcher,
            analyticService: analyticService,
            analyticCatalog: catalog,
            ruleStore: ruleStore);

        await vm.LoadAssignedAnalyticsAsync();
        vm.OpenRulesModalCommand.Execute(null);

        vm.StartCreateRuleCommand.Execute(null);
        Assert.True(vm.IsEditingRule);
        Assert.Equal("Nueva Regla de Alerta", vm.RuleFormTitle);

        vm.RuleEditName = "Alerta Mano Crítica";
        vm.RuleEditEventType = "hand_raised";
        vm.RuleEditSeverity = "Crítica";
        vm.RuleEditCooldownSeconds = 10;
        vm.RuleEditEnabled = true;

        await vm.SaveRuleCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditingRule);
        Assert.Null(vm.RuleErrorMessage);

        // Verify in RuleStore
        var storedRules = await ruleStore.ListByInstanceAsync("cam-1", "inst-hr");
        Assert.Single(storedRules);
        var r = storedRules[0];
        Assert.Equal("Alerta Mano Crítica", r.Name);
        Assert.Equal("inst-hr", r.AnalyticInstanceId);
        Assert.Equal("cam-1", r.CameraId);
        Assert.Equal(AlertSeverity.Critical, r.Severity);
        Assert.Equal(10000, r.CooldownMs);
        Assert.Equal(["hand_raised"], r.EventTypes);
        Assert.True(r.Enabled);

        // Verify in ViewModel list
        Assert.Single(vm.CameraRules);
        Assert.Equal("Alerta Mano Crítica", vm.CameraRules[0].Name);
        Assert.Equal("Crítica", vm.CameraRules[0].SeverityText);
        Assert.Equal(10, vm.CameraRules[0].CooldownSeconds);
    }

    [Fact]
    public async Task CameraViewModel_RulesModal_EditRule_UpdatesFieldsAndSaves()
    {
        var camera = new CameraView("cam-1", "Front Cam", "0", true, true, true, 30.0, null);
        var cameraService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var catalog = new StandardAnalyticCatalog();
        var ruleStore = new StubRuleStore();
        var dispatcher = Dispatcher.CurrentDispatcher;

        await analyticService.CreateAsync("cam-1", new CameraAnalyticWriteRequest(
            Id: "inst-hr",
            AnalyticTypeId: "hand_raise",
            Name: "Mano Levantada",
            Enabled: true));

        await ruleStore.SaveAsync(new AlertRule(
            Id: "rule-1",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-hr",
            Name: "Regla Original",
            Enabled: true,
            EventTypes: ["hand_raised"],
            Severity: AlertSeverity.Medium,
            CooldownMs: 5000));

        var vm = new CameraViewModel(
            camera,
            cameraService,
            dispatcher,
            analyticService: analyticService,
            analyticCatalog: catalog,
            ruleStore: ruleStore);

        await vm.LoadAssignedAnalyticsAsync();
        vm.OpenRulesModalCommand.Execute(null);
        await vm.LoadRulesForSelectedAnalyticAsync();

        Assert.Single(vm.CameraRules);
        var item = vm.CameraRules[0];

        vm.StartEditRuleCommand.Execute(item);
        Assert.True(vm.IsEditingRule);
        Assert.Equal("Editar Regla de Alerta", vm.RuleFormTitle);
        Assert.Equal("Regla Original", vm.RuleEditName);

        vm.RuleEditName = "Regla Modificada";
        vm.RuleEditEventType = "hand_lowered";
        vm.RuleEditSeverity = "Baja";
        vm.RuleEditCooldownSeconds = 20;

        await vm.SaveRuleCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditingRule);

        var updated = await ruleStore.GetByIdAsync("cam-1", "inst-hr", "rule-1");
        Assert.NotNull(updated);
        Assert.Equal("Regla Modificada", updated.Name);
        Assert.Equal(["hand_lowered"], updated.EventTypes);
        Assert.Equal(AlertSeverity.Low, updated.Severity);
        Assert.Equal(20000, updated.CooldownMs);
    }

    [Fact]
    public async Task CameraViewModel_RulesModal_DeleteRule_RemovesFromRuleStore()
    {
        var camera = new CameraView("cam-1", "Front Cam", "0", true, true, true, 30.0, null);
        var cameraService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var catalog = new StandardAnalyticCatalog();
        var ruleStore = new StubRuleStore();
        var dispatcher = Dispatcher.CurrentDispatcher;

        await analyticService.CreateAsync("cam-1", new CameraAnalyticWriteRequest(
            Id: "inst-hr",
            AnalyticTypeId: "hand_raise",
            Name: "Mano Levantada",
            Enabled: true));

        await ruleStore.SaveAsync(new AlertRule(
            Id: "rule-1",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-hr",
            Name: "Regla a Eliminar",
            Enabled: true,
            EventTypes: ["hand_raised"],
            Severity: AlertSeverity.High,
            CooldownMs: 3000));

        var vm = new CameraViewModel(
            camera,
            cameraService,
            dispatcher,
            analyticService: analyticService,
            analyticCatalog: catalog,
            ruleStore: ruleStore);

        await vm.LoadAssignedAnalyticsAsync();
        vm.OpenRulesModalCommand.Execute(null);
        await vm.LoadRulesForSelectedAnalyticAsync();

        Assert.Single(vm.CameraRules);
        var item = vm.CameraRules[0];

        await vm.DeleteRuleCommand.ExecuteAsync(item);

        Assert.Empty(vm.CameraRules);
        var stored = await ruleStore.ListByInstanceAsync("cam-1", "inst-hr");
        Assert.Empty(stored);
    }

    [Fact]
    public async Task CameraViewModel_RulesModal_MultipleAnalytics_SwitchingAnalyticUpdatesRulesAndEventTypes()
    {
        var camera = new CameraView("cam-1", "Front Cam", "0", true, true, true, 30.0, null);
        var cameraService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var catalog = new StandardAnalyticCatalog();
        var ruleStore = new StubRuleStore();
        var dispatcher = Dispatcher.CurrentDispatcher;

        await analyticService.CreateAsync("cam-1", new CameraAnalyticWriteRequest(
            Id: "inst-hr",
            AnalyticTypeId: "hand_raise",
            Name: "Mano Levantada",
            Enabled: true));

        await analyticService.CreateAsync("cam-1", new CameraAnalyticWriteRequest(
            Id: "inst-zi",
            AnalyticTypeId: "zone_intrusion",
            Name: "Intrusión en Zona",
            Enabled: true));

        await ruleStore.SaveAsync(new AlertRule(
            Id: "rule-hr",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-hr",
            Name: "Regla Mano",
            EventTypes: ["hand_raised"]));

        await ruleStore.SaveAsync(new AlertRule(
            Id: "rule-zi",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-zi",
            Name: "Regla Intrusión",
            EventTypes: ["zone_intrusion_started"]));

        var vm = new CameraViewModel(
            camera,
            cameraService,
            dispatcher,
            analyticService: analyticService,
            analyticCatalog: catalog,
            ruleStore: ruleStore);

        await vm.LoadAssignedAnalyticsAsync();
        Assert.True(vm.HasMultipleAssignedAnalytics);
        Assert.Equal(2, vm.AssignedAnalytics.Count);

        vm.OpenRulesModalCommand.Execute(null);

        // Preselected is first analytic (inst-hr)
        Assert.Equal("inst-hr", vm.SelectedRuleAnalytic!.InstanceId);
        Assert.Equal(["hand_raised", "hand_lowered"], vm.AvailableRuleEventTypes);
        Assert.Single(vm.CameraRules);
        Assert.Equal("Regla Mano", vm.CameraRules[0].Name);

        // Switch to second analytic (inst-zi)
        var secondAnalytic = vm.AssignedAnalytics.First(a => a.InstanceId == "inst-zi");
        vm.SelectedRuleAnalytic = secondAnalytic;
        await vm.LoadRulesForSelectedAnalyticAsync();

        Assert.Equal(["zone_intrusion_started", "zone_intrusion_ended"], vm.AvailableRuleEventTypes);
        Assert.Single(vm.CameraRules);
        Assert.Equal("Regla Intrusión", vm.CameraRules[0].Name);
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

    [Fact]
    public async Task MainViewModel_MetricsNavigation_OpensOverviewAndDetailCleanly()
    {
        var config = CreateTestConfig();
        var controller = new NodeHostController();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var vm = new MainViewModel(config, controller, dispatcher, _ => { });

        vm.SelectedNavIndex = 2;
        Assert.True(vm.IsMetricsSelected);
        Assert.True(vm.IsMetricsOverviewOpen);
        Assert.False(vm.IsMetricsDetailOpen);
        Assert.Null(vm.SelectedMetricsCamera);

        var cameraView = new CameraView("cam-1", "Cámara Principal", "0", true, true, true, 30.0, null);
        var camVm = new CameraViewModel(cameraView, new StubCameraManagementService(), dispatcher);
        vm.Cameras.Add(camVm);

        await vm.OpenCameraMetricsCommand.ExecuteAsync(camVm);
        Assert.True(vm.IsMetricsSelected);
        Assert.True(vm.IsMetricsDetailOpen);
        Assert.False(vm.IsMetricsOverviewOpen);
        Assert.Same(camVm, vm.SelectedMetricsCamera);

        await vm.BackToMetricsOverviewCommand.ExecuteAsync(null);
        Assert.True(vm.IsMetricsSelected);
        Assert.True(vm.IsMetricsOverviewOpen);
        Assert.False(vm.IsMetricsDetailOpen);
        Assert.Null(vm.SelectedMetricsCamera);
    }

    [Fact]
    public void EventItemViewModel_FriendlySpanishClassification_MapsCorrectly()
    {
        var ev = new HandEvent(
            Id: "ev-1",
            Type: "hand_raised",
            CameraId: "cam-1",
            TrackId: 42,
            Hand: "Right",
            Zone: "Zona Frente",
            Confidence: 0.88,
            Timestamp: DateTimeOffset.UtcNow);

        var vm = EventItemViewModel.Create(ev, "data/snapshots");

        Assert.Equal("Mano levantada detectada", vm.Title);
        Assert.Equal("hand_raised", vm.RawEventType);
        Assert.Equal("Zona Frente", vm.ZoneName);
        Assert.Contains("Zona Frente", vm.Description);
    }

    [Fact]
    public async Task CameraViewModel_UX10_5_ConfiguredRules_PopulatedAndEditableFromWorkspace()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();
        var ruleStore = new StubRuleStore();

        var analytic = await analyticService.CreateAsync("cam-1", new CameraAnalyticWriteRequest(
            Id: "inst-1",
            AnalyticTypeId: "hand_raise",
            Name: "Mano Levantada",
            Enabled: true,
            Configuration: new Dictionary<string, object?>()));

        await ruleStore.SaveAsync(new AlertRule(
            Id: "rule-1",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-1",
            Name: "Alerta Mano Arriba",
            Enabled: true,
            EventTypes: ["hand_raised"],
            Conditions: null,
            Severity: AlertSeverity.High,
            TitleTemplate: "{event_type}",
            DescriptionTemplate: "{camera_name}",
            CooldownMs: 5000,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));

        var vm = new CameraViewModel(
            new CameraView("cam-1", "Cámara 1", "0", true, true, true, 30.0, null),
            camService,
            dispatcher,
            analyticService: analyticService,
            ruleStore: ruleStore);

        await vm.LoadAssignedAnalyticsAsync();
        await vm.LoadConfiguredRulesAsync();

        Assert.True(vm.HasConfiguredRules);
        Assert.False(vm.HasNoConfiguredRules);
        Assert.Single(vm.ConfiguredRules);
        var item = vm.ConfiguredRules[0];
        Assert.Equal("Alerta Mano Arriba", item.Name);
        Assert.Equal("Alta", item.SeverityText);
        Assert.Equal("Mano levantada", item.EventTypesText);
        Assert.Equal("Activa", item.StatusText);
        Assert.Equal("5 seg", item.CooldownText);

        // Test editing from workspace opens modal
        vm.StartEditRuleFromWorkspaceCommand.Execute(item);
        Assert.True(vm.IsRulesModalOpen);
        Assert.True(vm.IsEditingRule);
        Assert.Equal("rule-1", vm.RuleEditId);
        Assert.Equal("Alerta Mano Arriba", vm.RuleEditName);

        // Test creating rule from workspace opens modal
        vm.CloseRulesModalCommand.Execute(null);
        Assert.False(vm.IsRulesModalOpen);
        vm.StartCreateRuleFromWorkspaceCommand.Execute(null);
        Assert.True(vm.IsRulesModalOpen);
        Assert.True(vm.IsEditingRule);
        Assert.Null(vm.RuleEditId);
    }

    [Fact]
    public async Task CameraViewModel_UX10_5_ConfiguredAnalyticsMetrics_DynamicAndReflectsOnlyAssignedAnalytics()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();
        var analyticService = new StubAnalyticManagementService();

        await analyticService.CreateAsync("cam-2", new CameraAnalyticWriteRequest(
            Id: "inst-hr",
            AnalyticTypeId: "hand_raise",
            Name: "Mano Levantada",
            Enabled: true,
            Configuration: new Dictionary<string, object?>()));

        var vm = new CameraViewModel(
            new CameraView("cam-2", "Cámara 2", "0", true, true, true, 30.0, null),
            camService,
            dispatcher,
            analyticService: analyticService);

        await vm.LoadAssignedAnalyticsAsync();

        Assert.True(vm.HasConfiguredAnalyticsMetrics);
        Assert.False(vm.HasNoConfiguredAnalyticsMetrics);
        Assert.Single(vm.ConfiguredAnalyticsMetrics);
        Assert.Equal("hand_raise", vm.ConfiguredAnalyticsMetrics[0].AnalyticTypeId);
        Assert.Equal("Mano Levantada", vm.ConfiguredAnalyticsMetrics[0].DisplayName);
        Assert.Equal("Activa", vm.ConfiguredAnalyticsMetrics[0].StatusText);
        Assert.Equal(0, vm.ConfiguredAnalyticsMetrics[0].EventsCount);
    }

    [Fact]
    public void CameraViewModel_UX10_5_Toggle_ChangesButtonTextAndCanonicalState()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var camService = new StubCameraManagementService();

        var vm = new CameraViewModel(
            new CameraView("cam-3", "Cámara 3", "0", true, true, true, 30.0, null),
            camService,
            dispatcher);

        Assert.True(vm.IsRunning);
        Assert.Equal("Detener", vm.ButtonText);
        Assert.Equal("En línea", vm.CanonicalStateText);
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

    private sealed class StubRuleStore : IRuleStore
    {
        private readonly List<AlertRule> _rules = [];

        public IReadOnlyList<AlertRule> GetRules(string cameraId) => _rules.Where(r => r.CameraId == cameraId).ToList();
        public IReadOnlyList<AlertRule> GetAllRules() => _rules.ToList();

        public Task<IReadOnlyList<AlertRule>> ListByInstanceAsync(string cameraId, string instanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AlertRule>>(_rules.Where(r => r.CameraId == cameraId && r.AnalyticInstanceId == instanceId).ToList());

        public Task<IReadOnlyList<AlertRule>> ListByCameraAsync(string cameraId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AlertRule>>(_rules.Where(r => r.CameraId == cameraId).ToList());

        public Task<IReadOnlyList<AlertRule>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AlertRule>>(_rules.ToList());

        public Task<AlertRule?> GetByIdAsync(string cameraId, string instanceId, string ruleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rules.FirstOrDefault(r => r.CameraId == cameraId && r.AnalyticInstanceId == instanceId && r.Id == ruleId));

        public Task<AlertRule> SaveAsync(AlertRule rule, CancellationToken cancellationToken = default)
        {
            _rules.RemoveAll(r => r.CameraId == rule.CameraId && r.AnalyticInstanceId == rule.AnalyticInstanceId && r.Id == rule.Id);
            _rules.Add(rule);
            return Task.FromResult(rule);
        }

        public Task<bool> DeleteAsync(string cameraId, string instanceId, string ruleId, CancellationToken cancellationToken = default)
        {
            var count = _rules.RemoveAll(r => r.CameraId == cameraId && r.AnalyticInstanceId == instanceId && r.Id == ruleId);
            return Task.FromResult(count > 0);
        }

        public Task<int> DeleteByCameraAsync(string cameraId, CancellationToken cancellationToken = default)
        {
            var count = _rules.RemoveAll(r => r.CameraId == cameraId);
            return Task.FromResult(count);
        }
    }
}
