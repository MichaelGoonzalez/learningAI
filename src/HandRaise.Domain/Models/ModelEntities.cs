using System.Text.Json.Serialization;
using HandRaise.Domain.Analytics;

namespace HandRaise.Domain.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelFormat
{
    Onnx,
    TorchScript,
    TensorRt,
    OpenVino,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapacityStatus
{
    Healthy,
    NearCapacity,
    OverCapacity
}

public sealed record ModelDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; }

    [JsonPropertyName("format")]
    public ModelFormat Format { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<InferenceCapability> Capabilities { get; init; }

    [JsonPropertyName("input_width")]
    public int InputWidth { get; init; }

    [JsonPropertyName("input_height")]
    public int InputHeight { get; init; }

    [JsonPropertyName("path")]
    public string Path { get; init; }

    [JsonPropertyName("class_count")]
    public int ClassCount { get; init; }

    [JsonPropertyName("keypoint_count")]
    public int KeypointCount { get; init; }

    [JsonPropertyName("confidence_threshold")]
    public float ConfidenceThreshold { get; init; }

    [JsonPropertyName("iou_threshold")]
    public float IouThreshold { get; init; }

    [JsonPropertyName("maximum_detections")]
    public int MaximumDetections { get; init; }

    [JsonPropertyName("preferred_device")]
    public string? PreferredDevice { get; init; }

    [JsonPropertyName("memory_estimate_mb")]
    public double MemoryEstimateMb { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonConstructor]
    public ModelDescriptor(
        string Id,
        string DisplayName,
        string Version,
        ModelFormat Format,
        IReadOnlyList<InferenceCapability> Capabilities,
        int InputWidth,
        int InputHeight,
        string Path,
        int ClassCount = 1,
        int KeypointCount = 17,
        float ConfidenceThreshold = 0.25f,
        float IouThreshold = 0.7f,
        int MaximumDetections = 300,
        string? PreferredDevice = null,
        double MemoryEstimateMb = 150.0,
        string? Sha256 = null,
        bool Enabled = true)
    {
        this.Id = Id;
        this.DisplayName = DisplayName;
        this.Version = Version;
        this.Format = Format;
        this.Capabilities = Capabilities ?? [];
        this.InputWidth = InputWidth;
        this.InputHeight = InputHeight;
        this.Path = Path;
        this.ClassCount = ClassCount;
        this.KeypointCount = KeypointCount;
        this.ConfidenceThreshold = ConfidenceThreshold;
        this.IouThreshold = IouThreshold;
        this.MaximumDetections = MaximumDetections;
        this.PreferredDevice = PreferredDevice;
        this.MemoryEstimateMb = MemoryEstimateMb;
        this.Sha256 = Sha256;
        this.Enabled = Enabled;
    }

    public ModelDescriptor(
        string Path,
        int InputWidth,
        int InputHeight,
        int ClassCount,
        int KeypointCount,
        float ConfidenceThreshold,
        float IouThreshold,
        int MaximumDetections)
        : this(
            Id: System.IO.Path.GetFileNameWithoutExtension(Path) is { Length: > 0 } fn ? fn : "model",
            DisplayName: System.IO.Path.GetFileNameWithoutExtension(Path) is { Length: > 0 } dn ? dn : "Model",
            Version: "1.0.0",
            Format: ModelFormat.Onnx,
            Capabilities: [InferenceCapability.PoseEstimation, InferenceCapability.ObjectDetection, InferenceCapability.Tracking],
            InputWidth: InputWidth,
            InputHeight: InputHeight,
            Path: Path,
            ClassCount: ClassCount,
            KeypointCount: KeypointCount,
            ConfidenceThreshold: ConfidenceThreshold,
            IouThreshold: IouThreshold,
            MaximumDetections: MaximumDetections)
    {
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("El ID del modelo es obligatorio.", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            throw new ArgumentException("El nombre del modelo es obligatorio.", nameof(DisplayName));
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new ArgumentException("La ruta del modelo es obligatoria.", nameof(Path));
        }

        if (InputWidth <= 0 || InputHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InputWidth), "El tamaño de entrada debe ser positivo.");
        }

        if (ClassCount <= 0 || KeypointCount < 0 || MaximumDetections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ClassCount), "Los parámetros de conteo deben ser válidos.");
        }

        if (ConfidenceThreshold is < 0 or > 1 || IouThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ConfidenceThreshold), "Los umbrales deben estar entre 0 y 1.");
        }
    }
}

public sealed record SelectedProviderInfo(
    [property: JsonPropertyName("provider_id")] string ProviderId,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("model_name")] string ModelName,
    [property: JsonPropertyName("covered_capabilities")] IReadOnlyList<InferenceCapability> CoveredCapabilities,
    [property: JsonPropertyName("device")] string? Device = null);

public sealed record CapabilityExecutionPlan(
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("required_capabilities")] IReadOnlyList<InferenceCapability> RequiredCapabilities,
    [property: JsonPropertyName("selected_providers")] IReadOnlyList<SelectedProviderInfo> SelectedProviders,
    [property: JsonPropertyName("active_analytics_count")] int ActiveAnalyticsCount,
    [property: JsonPropertyName("dependent_analytic_ids")] IReadOnlyList<string> DependentAnalyticIds);

public sealed record CameraCapacityDetail(
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("target_fps")] double TargetFps,
    [property: JsonPropertyName("actual_fps")] double ActualFps,
    [property: JsonPropertyName("providers")] IReadOnlyList<string> Providers,
    [property: JsonPropertyName("analytics_count")] int AnalyticsCount,
    [property: JsonPropertyName("inference_ms")] double InferenceMs,
    [property: JsonPropertyName("dropped_frames")] long DroppedFrames,
    [property: JsonPropertyName("status")] CapacityStatus Status);

public sealed record ExtendedCapacitySnapshot(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("site_id")] string SiteId,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("active_models")] IReadOnlyList<string> ActiveModels,
    [property: JsonPropertyName("estimated_vram_mb")] double EstimatedVramMb,
    [property: JsonPropertyName("aggregate_fps")] double AggregateFps,
    [property: JsonPropertyName("aggregate_inference_ms")] double AggregateInferenceMs,
    [property: JsonPropertyName("camera_count")] int CameraCount,
    [property: JsonPropertyName("active_analytic_count")] int ActiveAnalyticCount,
    [property: JsonPropertyName("capacity_status")] CapacityStatus CapacityStatus,
    [property: JsonPropertyName("cameras")] IReadOnlyList<CameraCapacityDetail> Cameras,
    [property: JsonPropertyName("target_frames_per_second")] double TargetFramesPerSecond = 5.0,
    [property: JsonPropertyName("estimated_maximum_cameras")] int? EstimatedMaximumCameras = null)
{
    [JsonPropertyName("targetFramesPerSecond")]
    public double TargetFramesPerSecondCamel => TargetFramesPerSecond;

    [JsonPropertyName("estimatedMaximumCameras")]
    public int? EstimatedMaximumCamerasCamel => EstimatedMaximumCameras;
}
