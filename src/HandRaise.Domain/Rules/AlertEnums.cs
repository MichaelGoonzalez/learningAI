using System.Text.Json.Serialization;

namespace HandRaise.Domain.Rules;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertSeverity
{
    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("high")]
    High,

    [JsonStringEnumMemberName("critical")]
    Critical
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertStatus
{
    [JsonStringEnumMemberName("open")]
    Open,

    [JsonStringEnumMemberName("acknowledged")]
    Acknowledged,

    [JsonStringEnumMemberName("resolved")]
    Resolved
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleOperator
{
    [JsonStringEnumMemberName("equals")]
    Equals,

    [JsonStringEnumMemberName("not_equals")]
    NotEquals,

    [JsonStringEnumMemberName("greater_than")]
    GreaterThan,

    [JsonStringEnumMemberName("greater_or_equal")]
    GreaterOrEqual,

    [JsonStringEnumMemberName("less_than")]
    LessThan,

    [JsonStringEnumMemberName("less_or_equal")]
    LessOrEqual,

    [JsonStringEnumMemberName("contains")]
    Contains
}
