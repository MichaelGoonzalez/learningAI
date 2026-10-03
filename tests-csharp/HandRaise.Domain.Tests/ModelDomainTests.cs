using System.Text.Json;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using Xunit;

namespace HandRaise.Domain.Tests;

public class ModelDomainTests
{
    [Fact]
    public void ModelFormat_Enum_SerializesCorrectly()
    {
        var model = new ModelDescriptor(
            Id: "test-model",
            DisplayName: "Test Model",
            Version: "1.0.0",
            Format: ModelFormat.Onnx,
            Capabilities: [InferenceCapability.PoseEstimation],
            InputWidth: 640,
            InputHeight: 640,
            Path: "models/test.onnx");

        var json = JsonSerializer.Serialize(model);
        Assert.Contains("\"format\":\"Onnx\"", json);

        var deserialized = JsonSerializer.Deserialize<ModelDescriptor>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(ModelFormat.Onnx, deserialized.Format);
    }

    [Fact]
    public void CapacityStatus_Enum_SerializesCorrectly()
    {
        var snapshot = new ExtendedCapacitySnapshot(
            NodeId: "node-1",
            SiteId: "site-1",
            Device: "GPU",
            ActiveModels: ["yolo26n-pose"],
            EstimatedVramMb: 150.0,
            AggregateFps: 30.0,
            AggregateInferenceMs: 12.5,
            CameraCount: 1,
            ActiveAnalyticCount: 2,
            CapacityStatus: CapacityStatus.Healthy,
            Cameras: []);

        var json = JsonSerializer.Serialize(snapshot);
        Assert.Contains("\"capacity_status\":\"Healthy\"", json);

        var deserialized = JsonSerializer.Deserialize<ExtendedCapacitySnapshot>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(CapacityStatus.Healthy, deserialized.CapacityStatus);
    }

    [Fact]
    public void ModelDescriptor_Validation_SucceedsForValidModel()
    {
        var model = new ModelDescriptor(
            Id: "valid-model",
            DisplayName: "Valid Model",
            Version: "1.0.0",
            Format: ModelFormat.Onnx,
            Capabilities: [InferenceCapability.PoseEstimation],
            InputWidth: 640,
            InputHeight: 640,
            Path: "models/valid.onnx");

        var exception = Record.Exception(() => model.Validate());
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("", "Name", "path.onnx", 640, 640)]
    [InlineData("id", "", "path.onnx", 640, 640)]
    [InlineData("id", "Name", "", 640, 640)]
    [InlineData("id", "Name", "path.onnx", 0, 640)]
    [InlineData("id", "Name", "path.onnx", 640, -1)]
    public void ModelDescriptor_Validation_FailsForInvalidParameters(
        string id, string name, string path, int width, int height)
    {
        var model = new ModelDescriptor(
            Id: id,
            DisplayName: name,
            Version: "1.0.0",
            Format: ModelFormat.Onnx,
            Capabilities: [InferenceCapability.PoseEstimation],
            InputWidth: width,
            InputHeight: height,
            Path: path);

        Assert.ThrowsAny<ArgumentException>(() => model.Validate());
    }

    [Fact]
    public void CapabilityExecutionPlan_SerializesAndDeserializes()
    {
        var plan = new CapabilityExecutionPlan(
            CameraId: "cam-1",
            RequiredCapabilities: [InferenceCapability.PoseEstimation, InferenceCapability.Tracking],
            SelectedProviders:
            [
                new SelectedProviderInfo(
                    ProviderId: "provider-1",
                    ModelId: "yolo26n-pose",
                    ModelName: "YOLO26 Nano Pose",
                    CoveredCapabilities: [InferenceCapability.PoseEstimation, InferenceCapability.Tracking],
                    Device: "gpu")
            ],
            ActiveAnalyticsCount: 2,
            DependentAnalyticIds: ["inst-1", "inst-2"]);

        var json = JsonSerializer.Serialize(plan);
        Assert.Contains("\"camera_id\":\"cam-1\"", json);
        Assert.Contains("\"active_analytics_count\":2", json);

        var deserialized = JsonSerializer.Deserialize<CapabilityExecutionPlan>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("cam-1", deserialized.CameraId);
        Assert.Equal(2, deserialized.ActiveAnalyticsCount);
        Assert.Single(deserialized.SelectedProviders);
        Assert.Equal(2, deserialized.DependentAnalyticIds.Count);
    }
}

