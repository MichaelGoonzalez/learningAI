using System.Text.Json.Serialization;

namespace HandRaise.Domain.Rules;

public sealed record RuleCondition(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("operator")] RuleOperator Operator,
    [property: JsonPropertyName("value")] object? Value)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Field))
        {
            throw new ArgumentException("El campo 'field' de la condición es obligatorio.");
        }

        if (Value == null)
        {
            throw new ArgumentException("El valor 'value' de la condición es obligatorio.");
        }
    }
}

public sealed record AlertRule(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("analytic_instance_id")] string AnalyticInstanceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("event_types")] IReadOnlyList<string>? EventTypes = null,
    [property: JsonPropertyName("conditions")] IReadOnlyList<RuleCondition>? Conditions = null,
    [property: JsonPropertyName("severity")] AlertSeverity Severity = AlertSeverity.Medium,
    [property: JsonPropertyName("title_template")] string TitleTemplate = "{event_type}",
    [property: JsonPropertyName("description_template")] string DescriptionTemplate = "",
    [property: JsonPropertyName("cooldown_ms")] long CooldownMs = 0,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("El identificador de la regla es obligatorio.", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(CameraId))
        {
            throw new ArgumentException("El ID de cámara es obligatorio.", nameof(CameraId));
        }

        if (string.IsNullOrWhiteSpace(AnalyticInstanceId))
        {
            throw new ArgumentException("El ID de instancia analítica es obligatorio.", nameof(AnalyticInstanceId));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("El nombre de la regla es obligatorio.", nameof(Name));
        }

        if (EventTypes == null || EventTypes.Count == 0 || EventTypes.All(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("La regla debe especificar al menos un tipo de evento en 'event_types'.");
        }

        if (CooldownMs < 0)
        {
            throw new ArgumentException("El tiempo de cooldown no puede ser negativo.", nameof(CooldownMs));
        }

        if (Conditions != null)
        {
            foreach (var condition in Conditions)
            {
                condition.Validate();
            }
        }
    }
}

public sealed record OperationalAlert(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("rule_id")] string RuleId,
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("analytic_instance_id")] string AnalyticInstanceId,
    [property: JsonPropertyName("source_event_id")] string SourceEventId,
    [property: JsonPropertyName("severity")] AlertSeverity Severity,
    [property: JsonPropertyName("status")] AlertStatus Status,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("acknowledged_at")] DateTimeOffset? AcknowledgedAt = null,
    [property: JsonPropertyName("resolved_at")] DateTimeOffset? ResolvedAt = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object?>? Metadata = null);
