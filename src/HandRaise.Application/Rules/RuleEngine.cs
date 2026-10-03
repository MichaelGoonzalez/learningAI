using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Rules;

namespace HandRaise.Application.Rules;

public sealed class RuleEngine : IRuleEngine
{
    private readonly IRuleProvider? _ruleProvider;
    private readonly IAlertStore? _alertStore;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _ruleLastAlertTime = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string EventId, string RuleId), bool> _processedEvents = new();

    public RuleEngine(IRuleProvider? ruleProvider = null, IAlertStore? alertStore = null)
    {
        _ruleProvider = ruleProvider;
        _alertStore = alertStore;
    }

    public async Task<IReadOnlyList<OperationalAlert>> ProcessEventAsync(
        AnalyticEvent ev, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var rules = _ruleProvider?.GetRules(ev.CameraId) ?? [];
        if (rules.Count == 0) return [];

        var alerts = Evaluate(ev, rules);
        if (alerts.Count == 0) return [];

        var savedAlerts = new List<OperationalAlert>(alerts.Count);
        foreach (var alert in alerts)
        {
            if (_alertStore != null)
            {
                var exists = await _alertStore.ExistsForEventAndRuleAsync(alert.SourceEventId, alert.RuleId, cancellationToken);
                if (exists) continue;

                var saved = await _alertStore.SaveAsync(alert, cancellationToken);
                savedAlerts.Add(saved);
            }
            else
            {
                savedAlerts.Add(alert);
            }
        }

        return savedAlerts;
    }

    public IReadOnlyList<OperationalAlert> Evaluate(AnalyticEvent ev, IReadOnlyList<AlertRule> rules)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (rules == null || rules.Count == 0) return [];

        var generatedAlerts = new List<OperationalAlert>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;

            // CameraId filter
            if (!string.Equals(rule.CameraId, ev.CameraId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // AnalyticInstanceId filter (if set on rule and event)
            if (!string.IsNullOrWhiteSpace(rule.AnalyticInstanceId) &&
                !string.IsNullOrWhiteSpace(ev.AnalyticInstanceId) &&
                !string.Equals(rule.AnalyticInstanceId, ev.AnalyticInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // EventType filter
            if (rule.EventTypes == null || rule.EventTypes.Count == 0 ||
                !rule.EventTypes.Contains(ev.EventType, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Idempotency check: (EventId, RuleId)
            var idempotencyKey = (ev.Id, rule.Id);
            if (_processedEvents.ContainsKey(idempotencyKey))
            {
                continue;
            }

            // Cooldown check
            if (rule.CooldownMs > 0 && _ruleLastAlertTime.TryGetValue(rule.Id, out var lastTime))
            {
                var elapsedMs = (ev.TimestampUtc - lastTime).TotalMilliseconds;
                if (elapsedMs >= 0 && elapsedMs < rule.CooldownMs)
                {
                    continue;
                }
            }

            // Conditions evaluation (All conditions must match - AND logic)
            if (rule.Conditions != null && rule.Conditions.Count > 0)
            {
                var allPassed = true;
                foreach (var condition in rule.Conditions)
                {
                    if (!EvaluateCondition(condition, ev))
                    {
                        allPassed = false;
                        break;
                    }
                }

                if (!allPassed) continue;
            }

            // Record alert generation for cooldown & idempotency
            _ruleLastAlertTime[rule.Id] = ev.TimestampUtc;
            _processedEvents.TryAdd(idempotencyKey, true);

            // Cleanup idempotency memory if grown large
            if (_processedEvents.Count > 10000)
            {
                _processedEvents.Clear();
            }

            var title = InterpolateTemplate(rule.TitleTemplate, ev, rule);
            var description = InterpolateTemplate(rule.DescriptionTemplate, ev, rule);

            var alert = new OperationalAlert(
                Id: $"alert-{Guid.NewGuid().ToString("N")[..8]}",
                RuleId: rule.Id,
                CameraId: ev.CameraId,
                AnalyticInstanceId: ev.AnalyticInstanceId,
                SourceEventId: ev.Id,
                Severity: rule.Severity,
                Status: AlertStatus.Open,
                Title: title,
                Description: description,
                CreatedAt: ev.TimestampUtc,
                AcknowledgedAt: null,
                ResolvedAt: null,
                Metadata: ev.Metadata);

            generatedAlerts.Add(alert);
        }

        return generatedAlerts;
    }

    public void Reset()
    {
        _ruleLastAlertTime.Clear();
        _processedEvents.Clear();
    }

    public static bool EvaluateCondition(RuleCondition condition, AnalyticEvent ev)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(ev);

        var (hasValue, actualValue) = ResolveFieldValue(condition.Field, ev);
        if (!hasValue)
        {
            return false;
        }

        return CompareValues(actualValue, condition.Operator, condition.Value);
    }

    public static (bool HasValue, object? Value) ResolveFieldValue(string field, AnalyticEvent ev)
    {
        if (string.IsNullOrWhiteSpace(field)) return (false, null);

        var normalizedField = field.Trim();

        if (string.Equals(normalizedField, "event_type", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedField, "eventType", StringComparison.OrdinalIgnoreCase))
        {
            return (true, ev.EventType);
        }

        if (string.Equals(normalizedField, "confidence", StringComparison.OrdinalIgnoreCase))
        {
            return ev.Confidence.HasValue ? (true, ev.Confidence.Value) : (false, null);
        }

        if (string.Equals(normalizedField, "track_id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedField, "trackId", StringComparison.OrdinalIgnoreCase))
        {
            return ev.TrackId.HasValue ? (true, ev.TrackId.Value) : (false, null);
        }

        if (string.Equals(normalizedField, "zone_id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedField, "zoneId", StringComparison.OrdinalIgnoreCase))
        {
            return ev.ZoneId != null ? (true, ev.ZoneId) : (false, null);
        }

        if (string.Equals(normalizedField, "camera_id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedField, "cameraId", StringComparison.OrdinalIgnoreCase))
        {
            return (true, ev.CameraId);
        }

        if (string.Equals(normalizedField, "analytic_instance_id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedField, "analyticInstanceId", StringComparison.OrdinalIgnoreCase))
        {
            return (true, ev.AnalyticInstanceId);
        }

        if (string.Equals(normalizedField, "analytic_type", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedField, "analyticType", StringComparison.OrdinalIgnoreCase))
        {
            return (true, ev.AnalyticType);
        }

        // Metadata prefix or direct lookup
        if (ev.Metadata != null)
        {
            var key = normalizedField;
            if (key.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
            {
                key = key["metadata.".Length..];
            }

            foreach (var kvp in ev.Metadata)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, kvp.Value);
                }
            }
        }

        return (false, null);
    }

    private static bool CompareValues(object? actual, RuleOperator op, object? expected)
    {
        var unwrappedActual = UnwrapValue(actual);
        var unwrappedExpected = UnwrapValue(expected);

        // Numeric comparison attempt
        if (TryToDouble(unwrappedActual, out var numActual) && TryToDouble(unwrappedExpected, out var numExpected))
        {
            return op switch
            {
                RuleOperator.Equals => Math.Abs(numActual - numExpected) < 1e-6,
                RuleOperator.NotEquals => Math.Abs(numActual - numExpected) >= 1e-6,
                RuleOperator.GreaterThan => numActual > numExpected,
                RuleOperator.GreaterOrEqual => numActual >= numExpected,
                RuleOperator.LessThan => numActual < numExpected,
                RuleOperator.LessOrEqual => numActual <= numExpected,
                RuleOperator.Contains => unwrappedActual?.ToString()?.Contains(unwrappedExpected?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
                _ => false
            };
        }

        // Boolean comparison attempt
        if (unwrappedActual is bool bActual && unwrappedExpected is bool bExpected)
        {
            return op switch
            {
                RuleOperator.Equals => bActual == bExpected,
                RuleOperator.NotEquals => bActual != bExpected,
                _ => false
            };
        }

        // String comparison fallback
        var strActual = unwrappedActual?.ToString() ?? "";
        var strExpected = unwrappedExpected?.ToString() ?? "";

        return op switch
        {
            RuleOperator.Equals => string.Equals(strActual, strExpected, StringComparison.OrdinalIgnoreCase),
            RuleOperator.NotEquals => !string.Equals(strActual, strExpected, StringComparison.OrdinalIgnoreCase),
            RuleOperator.Contains => strActual.Contains(strExpected, StringComparison.OrdinalIgnoreCase),
            RuleOperator.GreaterThan => string.Compare(strActual, strExpected, StringComparison.OrdinalIgnoreCase) > 0,
            RuleOperator.GreaterOrEqual => string.Compare(strActual, strExpected, StringComparison.OrdinalIgnoreCase) >= 0,
            RuleOperator.LessThan => string.Compare(strActual, strExpected, StringComparison.OrdinalIgnoreCase) < 0,
            RuleOperator.LessOrEqual => string.Compare(strActual, strExpected, StringComparison.OrdinalIgnoreCase) <= 0,
            _ => false
        };
    }

    private static object? UnwrapValue(object? val)
    {
        if (val is JsonElement elem)
        {
            return elem.ValueKind switch
            {
                JsonValueKind.Number when elem.TryGetInt64(out var l) => l,
                JsonValueKind.Number when elem.TryGetDouble(out var d) => d,
                JsonValueKind.String => elem.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => elem.ToString()
            };
        }
        return val;
    }

    private static bool TryToDouble(object? val, out double result)
    {
        result = 0;
        if (val == null) return false;

        switch (val)
        {
            case sbyte sb: result = sb; return true;
            case byte b: result = b; return true;
            case short s: result = s; return true;
            case ushort us: result = us; return true;
            case int i: result = i; return true;
            case uint ui: result = ui; return true;
            case long l: result = l; return true;
            case ulong ul: result = ul; return true;
            case float f: result = f; return true;
            case double d: result = d; return true;
            case decimal dec: result = (double)dec; return true;
            case string str when double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                return false;
        }
    }

    public static string InterpolateTemplate(string? template, AnalyticEvent ev, AlertRule rule)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";

        var text = template;
        text = text.Replace("{event_type}", ev.EventType, StringComparison.OrdinalIgnoreCase);
        text = text.Replace("{camera_id}", ev.CameraId, StringComparison.OrdinalIgnoreCase);
        text = text.Replace("{track_id}", ev.TrackId?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        text = text.Replace("{zone_id}", ev.ZoneId ?? "", StringComparison.OrdinalIgnoreCase);
        text = text.Replace("{confidence}", ev.Confidence?.ToString("0.##", CultureInfo.InvariantCulture) ?? "", StringComparison.OrdinalIgnoreCase);
        text = text.Replace("{severity}", rule.Severity.ToString().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        text = text.Replace("{rule_name}", rule.Name, StringComparison.OrdinalIgnoreCase);

        if (ev.Metadata != null)
        {
            foreach (var kvp in ev.Metadata)
            {
                var valStr = kvp.Value?.ToString() ?? "";
                text = text.Replace($"{{metadata.{kvp.Key}}}", valStr, StringComparison.OrdinalIgnoreCase);
                text = text.Replace($"{{{kvp.Key}}}", valStr, StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }
}
