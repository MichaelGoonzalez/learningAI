using System.Windows.Threading;
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
}
