using System.Text.Json.Serialization;

namespace HandRaise.Domain.Zones;

public readonly record struct Point2D(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

