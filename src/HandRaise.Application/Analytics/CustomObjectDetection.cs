using System.Globalization;
using System.Text.Json;
using HandRaise.Application.Capture;
using HandRaise.Application.Inference;
using HandRaise.Domain.Analytics;

namespace HandRaise.Application.Analytics;

// Marker carried by the existing atomic evaluator set. Objects never enter the person tracker.
public sealed class CustomObjectEvaluator(CameraAnalyticInstance instance) : IAnalyticEvaluator
{
    public const string TypeId = "custom_object_detection";
    public static bool IsCustom(string? type) => string.Equals(type?.Trim(), TypeId, StringComparison.OrdinalIgnoreCase);
    public string AnalyticTypeId => TypeId;
    public string InstanceId => instance.Id;
    public bool IsEnabled => instance.Enabled;
    public string ModelId => instance.Configuration?.GetValueOrDefault("model_id")?.ToString() ?? "";
    public double Confidence
    {
        get
        {
            var raw = instance.Configuration?.GetValueOrDefault("confidence_threshold");
            var number = raw switch
            {
                double d => d, float f => f, int i => i, decimal d => (double)d,
                JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out var value) => value,
                _ => double.TryParse(raw?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : .5
            };
            return double.IsFinite(number) ? Math.Clamp(number, 0, 1) : .5;
        }
    }
    public AnalyticEvaluationResult Evaluate(AnalyticFrameContext context) => AnalyticEvaluationResult.Empty;
    public void Reset() { }
}

public sealed record ObjectDetection(string ModelId, string ModelName, string Version, Guid ClassId,
    string ClassName, int ClassIndex, float Confidence, BoundingBox Box);
public sealed record CustomFrameResult(IReadOnlyList<ObjectDetection> Detections, IReadOnlyList<AnalyticEvent> Events,
    IReadOnlyDictionary<string, string> Errors);

public interface ICustomObjectRuntime : IAsyncDisposable
{
    Task<CustomFrameResult> ProcessAsync(string cameraId, ImageFrame frame, DateTimeOffset timestamp,
        IReadOnlyList<CustomObjectEvaluator> analytics, CancellationToken ct);
}

public interface IObjectFrameOverlay
{
    VideoFrame DrawObjects(VideoFrame frame, IReadOnlyList<ObjectDetection> detections);
}
