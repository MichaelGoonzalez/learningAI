using System.Text.Json.Serialization;
using HandRaise.Domain.Rules;

namespace HandRaise.Domain.Notifications;

public sealed record NotificationDestination(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] NotificationDestinationType Type,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("configuration")] IReadOnlyDictionary<string, object?>? Configuration = null,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("El ID del destino de notificación es obligatorio.", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("El nombre del destino de notificación es obligatorio.", nameof(Name));
        }

        if (Configuration == null)
        {
            throw new ArgumentException("La configuración del destino de notificación es obligatoria.", nameof(Configuration));
        }

        switch (Type)
        {
            case NotificationDestinationType.Webhook:
                if (!Configuration.TryGetValue("url", out var urlVal) || urlVal is null || string.IsNullOrWhiteSpace(urlVal.ToString()))
                {
                    throw new ArgumentException("La configuración de webhook requiere el campo 'url'.", nameof(Configuration));
                }
                var urlStr = urlVal.ToString()!;
                if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ArgumentException($"La URL del webhook '{urlStr}' no es válida. Debe ser http o https.", nameof(Configuration));
                }
                break;

            case NotificationDestinationType.Mqtt:
                if (!Configuration.TryGetValue("broker", out var brokerVal) || brokerVal is null || string.IsNullOrWhiteSpace(brokerVal.ToString()))
                {
                    throw new ArgumentException("La configuración MQTT requiere el campo 'broker'.", nameof(Configuration));
                }
                break;
        }
    }
}

public sealed record NotificationPolicy(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("destination_id")] string DestinationId = "",
    [property: JsonPropertyName("severity_filter")] IReadOnlyList<AlertSeverity>? SeverityFilter = null,
    [property: JsonPropertyName("camera_ids")] IReadOnlyList<string>? CameraIds = null,
    [property: JsonPropertyName("analytic_type_ids")] IReadOnlyList<string>? AnalyticTypeIds = null,
    [property: JsonPropertyName("alert_status_filter")] IReadOnlyList<AlertStatus>? AlertStatusFilter = null,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("El ID de la política de notificación es obligatorio.", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("El nombre de la política de notificación es obligatorio.", nameof(Name));
        }

        if (string.IsNullOrWhiteSpace(DestinationId))
        {
            throw new ArgumentException("El 'destination_id' es obligatorio en la política de notificación.", nameof(DestinationId));
        }
    }

    public bool Matches(OperationalAlert alert, string? analyticTypeId = null)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (!Enabled) return false;

        if (SeverityFilter != null && SeverityFilter.Count > 0)
        {
            if (!SeverityFilter.Contains(alert.Severity))
            {
                return false;
            }
        }

        if (CameraIds != null && CameraIds.Count > 0)
        {
            if (!CameraIds.Contains(alert.CameraId, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (AnalyticTypeIds != null && AnalyticTypeIds.Count > 0)
        {
            var matchesType = false;
            if (!string.IsNullOrWhiteSpace(analyticTypeId) && AnalyticTypeIds.Contains(analyticTypeId, StringComparer.OrdinalIgnoreCase))
            {
                matchesType = true;
            }
            else if (!string.IsNullOrWhiteSpace(alert.AnalyticInstanceId) && AnalyticTypeIds.Contains(alert.AnalyticInstanceId, StringComparer.OrdinalIgnoreCase))
            {
                matchesType = true;
            }

            if (!matchesType) return false;
        }

        if (AlertStatusFilter != null && AlertStatusFilter.Count > 0)
        {
            if (!AlertStatusFilter.Contains(alert.Status))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record NotificationAttempt(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("alert_id")] string AlertId,
    [property: JsonPropertyName("policy_id")] string PolicyId,
    [property: JsonPropertyName("destination_id")] string DestinationId,
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("status")] NotificationStatus Status,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("response_code")] int? ResponseCode = null,
    [property: JsonPropertyName("error_sanitized")] string? ErrorSanitized = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record WebhookPayload(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("site_id")] string SiteId,
    [property: JsonPropertyName("alert")] OperationalAlert Alert,
    [property: JsonPropertyName("dispatched_at")] DateTimeOffset DispatchedAt,
    [property: JsonPropertyName("is_test")] bool IsTest = false);
