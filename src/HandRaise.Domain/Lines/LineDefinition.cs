using System.Text.Json.Serialization;
using HandRaise.Domain.Zones;

namespace HandRaise.Domain.Lines;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LineDirectionMode
{
    [JsonStringEnumMemberName("bidirectional")]
    Bidirectional,

    [JsonStringEnumMemberName("a_to_b")]
    AToB,

    [JsonStringEnumMemberName("b_to_a")]
    BToA
}

public sealed record LineDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("point_a")] Point2D PointA,
    [property: JsonPropertyName("point_b")] Point2D PointB,
    [property: JsonPropertyName("direction_mode")] LineDirectionMode DirectionMode = LineDirectionMode.Bidirectional,
    [property: JsonPropertyName("enabled")] bool Enabled = true)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("El id de línea es obligatorio.", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("El nombre de línea es obligatorio.", nameof(Name));
        }

        if (!double.IsFinite(PointA.X) || !double.IsFinite(PointA.Y) ||
            PointA.X is < 0 or > 1 || PointA.Y is < 0 or > 1)
        {
            throw new ArgumentException("Las coordenadas de PointA deben ser números finitos entre 0.0 y 1.0.");
        }

        if (!double.IsFinite(PointB.X) || !double.IsFinite(PointB.Y) ||
            PointB.X is < 0 or > 1 || PointB.Y is < 0 or > 1)
        {
            throw new ArgumentException("Las coordenadas de PointB deben ser números finitos entre 0.0 y 1.0.");
        }

        if (Math.Abs(PointA.X - PointB.X) < 1e-6 && Math.Abs(PointA.Y - PointB.Y) < 1e-6)
        {
            throw new ArgumentException("PointA y PointB no pueden ser idénticos.");
        }
    }
}

