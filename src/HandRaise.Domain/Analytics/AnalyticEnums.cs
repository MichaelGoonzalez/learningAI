using System.Text.Json.Serialization;

namespace HandRaise.Domain.Analytics;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalyticStatus
{
    Pending,
    Active,
    Degraded,
    Inactive,
    Error,
    Unsupported
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalyticCategory
{
    General,
    Security,
    Safety,
    Operations,
    Logistics,
    Quality
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InferenceCapability
{
    ObjectDetection,
    PoseEstimation,
    Tracking,
    Classification,
    Segmentation
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalyticFeature
{
    Zones,
    Lines,
    Points,
    Sensitivity,
    Snapshots,
    Tracking,
    Schedules
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParameterType
{
    Number,
    Boolean,
    String,
    Select
}
