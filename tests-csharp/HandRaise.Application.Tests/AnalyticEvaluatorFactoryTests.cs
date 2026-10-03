using HandRaise.Application.Analytics;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Detection;

namespace HandRaise.Application.Tests;

public sealed class AnalyticEvaluatorFactoryTests
{
    private readonly AnalyticEvaluatorFactory _factory = new();
    private readonly DetectionOptions _defaultOptions = new()
    {
        KeypointConfidence = 0.5,
        ShoulderMarginRatio = 0.15,
        StrictMode = false,
        StrictMarginPixels = 0.0,
        ConsecutiveFrames = 3,
        LowerConsecutiveFrames = 3,
        Cooldown = TimeSpan.FromMilliseconds(1000),
        TrackTimeToLive = TimeSpan.FromMilliseconds(5000)
    };

    [Fact]
    public void FactoryCreatesEvaluatorWithConfiguredParameters()
    {
        var instance = new CameraAnalyticInstance(
            Id: "an-cam1-custom",
            CameraId: "cam-1",
            AnalyticTypeId: "hand_raise",
            Name: "Custom HandRaise",
            Enabled: true,
            Status: AnalyticStatus.Active,
            Configuration: new Dictionary<string, object?>
            {
                ["strict_mode"] = true,
                ["consecutive_frames"] = 6,
                ["cooldown_ms"] = 2500
            });

        var evaluator = _factory.CreateEvaluator(instance, _defaultOptions);
        Assert.NotNull(evaluator);
        Assert.Equal("hand_raise", evaluator.AnalyticTypeId);
        Assert.Equal("an-cam1-custom", evaluator.InstanceId);
        Assert.True(evaluator.IsEnabled);
    }

    [Fact]
    public void FactoryCreatesEvaluatorWithDefaultsWhenConfigEmpty()
    {
        var instance = new CameraAnalyticInstance(
            Id: "an-cam1-default",
            CameraId: "cam-1",
            AnalyticTypeId: "hand_raise",
            Name: "Default HandRaise",
            Enabled: false,
            Status: AnalyticStatus.Inactive);

        var evaluator = _factory.CreateEvaluator(instance, _defaultOptions);
        Assert.NotNull(evaluator);
        Assert.Equal("hand_raise", evaluator.AnalyticTypeId);
        Assert.False(evaluator.IsEnabled);
    }

    [Fact]
    public void FactoryThrowsOnUnsupportedType()
    {
        var instance = new CameraAnalyticInstance(
            Id: "an-cam1-unsupported",
            CameraId: "cam-1",
            AnalyticTypeId: "unknown_future_analytic",
            Name: "Unknown",
            Enabled: true,
            Status: AnalyticStatus.Active);

        Assert.Throws<NotSupportedException>(() => _factory.CreateEvaluator(instance, _defaultOptions));
    }

    [Fact]
    public void FactoryCreatesPersonPresenceEvaluatorWithConfiguredParameters()
    {
        var instance = new CameraAnalyticInstance(
            Id: "an-cam1-presence-custom",
            CameraId: "cam-1",
            AnalyticTypeId: "person_presence",
            Name: "Custom Presence",
            Enabled: true,
            Status: AnalyticStatus.Active,
            Configuration: new Dictionary<string, object?>
            {
                ["confidence_threshold"] = 0.80,
                ["min_presence_ms"] = 3000,
                ["absence_grace_ms"] = 2500,
                ["emit_snapshot"] = false
            },
            AssignedZoneIds: ["zone-1"]);

        var evaluator = _factory.CreateEvaluator(instance, _defaultOptions);
        Assert.NotNull(evaluator);
        Assert.IsType<PersonPresenceAnalyticEvaluator>(evaluator);
        Assert.Equal("person_presence", evaluator.AnalyticTypeId);
        Assert.Equal("an-cam1-presence-custom", evaluator.InstanceId);
        Assert.True(evaluator.IsEnabled);
    }

    [Fact]
    public void FactoryCreatesZoneIntrusionEvaluatorWithConfiguredParameters()
    {
        var instance = new CameraAnalyticInstance(
            Id: "an-cam1-intrusion-custom",
            CameraId: "cam-1",
            AnalyticTypeId: "zone_intrusion",
            Name: "Custom Intrusion",
            Enabled: true,
            Status: AnalyticStatus.Active,
            Configuration: new Dictionary<string, object?>
            {
                ["confidence_threshold"] = 0.75,
                ["entry_delay_ms"] = 1500,
                ["exit_grace_ms"] = 2000,
                ["emit_snapshot"] = false
            },
            AssignedZoneIds: ["restricted-vault"]);

        var evaluator = _factory.CreateEvaluator(instance, _defaultOptions);
        Assert.NotNull(evaluator);
        Assert.IsType<ZoneIntrusionAnalyticEvaluator>(evaluator);
        Assert.Equal("zone_intrusion", evaluator.AnalyticTypeId);
        Assert.Equal("an-cam1-intrusion-custom", evaluator.InstanceId);
        Assert.True(evaluator.IsEnabled);
    }
}
