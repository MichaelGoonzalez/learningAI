using System.Text.Json;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Detection;

namespace HandRaise.Application.Analytics;

public class AnalyticEvaluatorFactory : IAnalyticEvaluatorFactory
{
    public IAnalyticEvaluator CreateEvaluator(CameraAnalyticInstance instance, DetectionOptions defaultOptions)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(defaultOptions);

        if (string.Equals(instance.AnalyticTypeId, "hand_raise", StringComparison.OrdinalIgnoreCase))
        {
            var strictMode = ExtractBoolean(instance.Configuration, "strict_mode", defaultOptions.StrictMode);
            var consecutiveFrames = ExtractInt(instance.Configuration, "consecutive_frames", defaultOptions.ConsecutiveFrames);
            var cooldownMs = ExtractInt(instance.Configuration, "cooldown_ms", (int)defaultOptions.Cooldown.TotalMilliseconds);

            var options = new DetectionOptions
            {
                KeypointConfidence = defaultOptions.KeypointConfidence,
                ShoulderMarginRatio = defaultOptions.ShoulderMarginRatio,
                StrictMode = strictMode,
                StrictMarginPixels = defaultOptions.StrictMarginPixels,
                ConsecutiveFrames = consecutiveFrames,
                LowerConsecutiveFrames = defaultOptions.LowerConsecutiveFrames,
                Cooldown = TimeSpan.FromMilliseconds(cooldownMs),
                TrackTimeToLive = defaultOptions.TrackTimeToLive
            };

            return new HandRaiseAnalyticEvaluator(options, instance.Id)
            {
                IsEnabled = instance.Enabled
            };
        }

        if (string.Equals(instance.AnalyticTypeId, "person_presence", StringComparison.OrdinalIgnoreCase))
        {
            var confidence = ExtractDouble(instance.Configuration, "confidence_threshold", 0.60);
            var minPresenceMs = ExtractDouble(instance.Configuration, "min_presence_ms", 1000);
            var absenceGraceMs = ExtractDouble(instance.Configuration, "absence_grace_ms", 1500);
            var emitSnapshot = ExtractBoolean(instance.Configuration, "emit_snapshot", true);

            return new PersonPresenceAnalyticEvaluator(
                instanceId: instance.Id,
                assignedZoneIds: instance.AssignedZoneIds,
                confidenceThreshold: confidence,
                minPresenceMs: minPresenceMs,
                absenceGraceMs: absenceGraceMs,
                emitSnapshot: emitSnapshot)
            {
                IsEnabled = instance.Enabled
            };
        }

        if (string.Equals(instance.AnalyticTypeId, "zone_intrusion", StringComparison.OrdinalIgnoreCase))
        {
            var confidence = ExtractDouble(instance.Configuration, "confidence_threshold", 0.60);
            var entryDelayMs = ExtractDouble(instance.Configuration, "entry_delay_ms", 500);
            var exitGraceMs = ExtractDouble(instance.Configuration, "exit_grace_ms", 1000);
            var emitSnapshot = ExtractBoolean(instance.Configuration, "emit_snapshot", true);

            return new ZoneIntrusionAnalyticEvaluator(
                instanceId: instance.Id,
                assignedZoneIds: instance.AssignedZoneIds,
                confidenceThreshold: confidence,
                entryDelayMs: entryDelayMs,
                exitGraceMs: exitGraceMs,
                emitSnapshot: emitSnapshot)
            {
                IsEnabled = instance.Enabled
            };
        }

        throw new NotSupportedException($"El tipo de analítica '{instance.AnalyticTypeId}' no tiene evaluador disponible.");
    }

    private static double ExtractDouble(IReadOnlyDictionary<string, object?>? config, string key, double defaultValue)
    {
        if (config == null || !config.TryGetValue(key, out var val) || val == null)
            return defaultValue;

        switch (val)
        {
            case sbyte s: return s;
            case byte b: return b;
            case short s: return s;
            case ushort u: return u;
            case int i: return i;
            case uint u: return u;
            case long l: return l;
            case ulong u: return u;
            case float f: return f;
            case double d: return d;
            case decimal dec: return (double)dec;
            case JsonElement elem when elem.ValueKind == JsonValueKind.Number && elem.TryGetDouble(out var d): return d;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed): return parsed;
            default: return defaultValue;
        }
    }

    private static bool ExtractBoolean(IReadOnlyDictionary<string, object?>? config, string key, bool defaultValue)
    {
        if (config == null || !config.TryGetValue(key, out var val) || val == null)
            return defaultValue;

        if (val is bool b) return b;
        if (val is JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.True) return true;
            if (elem.ValueKind == JsonValueKind.False) return false;
            if (elem.ValueKind == JsonValueKind.String && bool.TryParse(elem.GetString(), out var parsedBool))
                return parsedBool;
        }
        if (val is string s && bool.TryParse(s, out var parsed)) return parsed;
        return defaultValue;
    }

    private static int ExtractInt(IReadOnlyDictionary<string, object?>? config, string key, int defaultValue)
    {
        if (config == null || !config.TryGetValue(key, out var val) || val == null)
            return defaultValue;

        switch (val)
        {
            case int i: return i;
            case long l: return (int)l;
            case double d: return (int)d;
            case float f: return (int)f;
            case decimal dec: return (int)dec;
            case JsonElement elem when elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var i): return i;
            case string s when int.TryParse(s, out var parsed): return parsed;
            default: return defaultValue;
        }
    }
}
