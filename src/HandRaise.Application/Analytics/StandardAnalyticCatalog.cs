using System.Text.Json;
using HandRaise.Domain.Analytics;

namespace HandRaise.Application.Analytics;

public class StandardAnalyticCatalog : IAnalyticCatalog
{
    public static readonly AnalyticDefinition CustomObjectDefinition = new(
        CustomObjectEvaluator.TypeId, "Detección de objetos personalizada", "Reconoce los objetos de un modelo entrenado por usted.",
        AnalyticCategory.General, "1.0.0", [InferenceCapability.ObjectDetection], [], ["custom_object_detected"],
        [new("model_id", "Modelo y versión", ParameterType.String, ""),
         new("confidence_threshold", "Confianza mínima", ParameterType.Number, .5, Min: .05, Max: 1, Step: .05)]);
    public static readonly AnalyticDefinition HandRaiseDefinition = new(
        Id: "hand_raise",
        DisplayName: "Mano Levantada",
        Description: "Detecta personas con una o ambas manos alzadas por encima del hombro.",
        Category: AnalyticCategory.Operations,
        Version: "1.0.0",
        RequiredCapabilities:
        [
            InferenceCapability.PoseEstimation,
            InferenceCapability.Tracking
        ],
        Features:
        [
            AnalyticFeature.Zones,
            AnalyticFeature.Snapshots,
            AnalyticFeature.Tracking,
            AnalyticFeature.Sensitivity
        ],
        ProducedEventTypes:
        [
            "hand_raised",
            "hand_lowered"
        ],
        Parameters:
        [
            new ParameterDefinition(
                Key: "strict_mode",
                Label: "Modo Estricto",
                Type: ParameterType.Boolean,
                DefaultValue: false,
                Description: "Exige que la muñeca supere el margen superior con holgura estricta."),
            new ParameterDefinition(
                Key: "consecutive_frames",
                Label: "Cuadros de confirmación",
                Type: ParameterType.Number,
                DefaultValue: 3,
                Min: 1,
                Max: 30,
                Step: 1,
                Description: "Número de cuadros de video requeridos para confirmar el gesto."),
            new ParameterDefinition(
                Key: "cooldown_ms",
                Label: "Tiempo de espera entre alertas (ms)",
                Type: ParameterType.Number,
                DefaultValue: 1000,
                Min: 0,
                Max: 10000,
                Step: 100,
                Description: "Tiempo mínimo antes de volver a emitir una alerta para la misma persona.")
        ]);

    public static readonly AnalyticDefinition PersonPresenceDefinition = new(
        Id: "person_presence",
        DisplayName: "Presencia de Personas",
        Description: "Detecta la presencia de una o más personas dentro de una zona durante un tiempo configurable.",
        Category: AnalyticCategory.Operations,
        Version: "1.0.0",
        RequiredCapabilities:
        [
            InferenceCapability.ObjectDetection,
            InferenceCapability.Tracking
        ],
        Features:
        [
            AnalyticFeature.Zones,
            AnalyticFeature.Sensitivity,
            AnalyticFeature.Snapshots
        ],
        ProducedEventTypes:
        [
            "person_presence_started",
            "person_presence_ended"
        ],
        Parameters:
        [
            new ParameterDefinition(
                Key: "confidence_threshold",
                Label: "Umbral de confianza",
                Type: ParameterType.Number,
                DefaultValue: 0.60,
                Min: 0.10,
                Max: 0.95,
                Step: 0.05,
                Description: "Umbral mínimo de confianza de detección."),
            new ParameterDefinition(
                Key: "min_presence_ms",
                Label: "Tiempo mínimo de presencia",
                Type: ParameterType.Number,
                DefaultValue: 1000,
                Min: 0,
                Max: 60000,
                Step: 250,
                Description: "Tiempo mínimo de permanencia continua antes de confirmar la presencia."),
            new ParameterDefinition(
                Key: "absence_grace_ms",
                Label: "Tolerancia de ausencia",
                Type: ParameterType.Number,
                DefaultValue: 1500,
                Min: 0,
                Max: 30000,
                Step: 250,
                Description: "Tiempo de espera antes de considerar finalizada la presencia."),
            new ParameterDefinition(
                Key: "emit_snapshot",
                Label: "Guardar evidencia",
                Type: ParameterType.Boolean,
                DefaultValue: true,
                Description: "Genera una captura visual al iniciar la presencia.")
        ]);

    public static readonly AnalyticDefinition ZoneIntrusionDefinition = new(
        Id: "zone_intrusion",
        DisplayName: "Intrusión en Zona Restringida",
        Description: "Detecta la entrada y permanencia de personas dentro de una o más zonas configuradas.",
        Category: AnalyticCategory.Security,
        Version: "1.0.0",
        RequiredCapabilities:
        [
            InferenceCapability.ObjectDetection,
            InferenceCapability.Tracking
        ],
        Features:
        [
            AnalyticFeature.Zones,
            AnalyticFeature.Sensitivity,
            AnalyticFeature.Snapshots,
            AnalyticFeature.Tracking,
            AnalyticFeature.Schedules
        ],
        ProducedEventTypes:
        [
            "zone_intrusion_started",
            "zone_intrusion_ended"
        ],
        Parameters:
        [
            new ParameterDefinition(
                Key: "confidence_threshold",
                Label: "Umbral de confianza",
                Type: ParameterType.Number,
                DefaultValue: 0.60,
                Min: 0.10,
                Max: 0.95,
                Step: 0.05,
                Description: "Umbral mínimo de confianza de detección."),
            new ParameterDefinition(
                Key: "entry_delay_ms",
                Label: "Retardo de entrada",
                Type: ParameterType.Number,
                DefaultValue: 500,
                Min: 0,
                Max: 60000,
                Step: 250,
                Description: "Tiempo de permanencia en zona antes de confirmar la intrusión."),
            new ParameterDefinition(
                Key: "exit_grace_ms",
                Label: "Tolerancia de salida",
                Type: ParameterType.Number,
                DefaultValue: 1000,
                Min: 0,
                Max: 30000,
                Step: 250,
                Description: "Tiempo de espera antes de considerar finalizada la intrusión."),
            new ParameterDefinition(
                Key: "emit_snapshot",
                Label: "Guardar evidencia",
                Type: ParameterType.Boolean,
                DefaultValue: true,
                Description: "Genera una captura visual al iniciar la intrusión.")
        ]);

    public static readonly AnalyticDefinition LineCrossingDefinition = new(
        Id: "line_crossing",
        DisplayName: "Cruce de Línea",
        Description: "Detecta cuando una persona cruza una línea virtual configurada y determina la dirección del cruce.",
        Category: AnalyticCategory.Operations,
        Version: "1.0.0",
        RequiredCapabilities:
        [
            InferenceCapability.ObjectDetection,
            InferenceCapability.Tracking
        ],
        Features:
        [
            AnalyticFeature.Lines,
            AnalyticFeature.Sensitivity,
            AnalyticFeature.Snapshots,
            AnalyticFeature.Tracking
        ],
        ProducedEventTypes:
        [
            "line_crossed"
        ],
        Parameters:
        [
            new ParameterDefinition(
                Key: "confidence_threshold",
                Label: "Umbral de confianza",
                Type: ParameterType.Number,
                DefaultValue: 0.60,
                Min: 0.10,
                Max: 0.95,
                Step: 0.05,
                Description: "Umbral mínimo de confianza de detección."),
            new ParameterDefinition(
                Key: "crossing_cooldown_ms",
                Label: "Tiempo de espera entre alertas (ms)",
                Type: ParameterType.Number,
                DefaultValue: 1500,
                Min: 0,
                Max: 30000,
                Step: 250,
                Description: "Tiempo mínimo antes de registrar un nuevo cruce para la misma persona."),
            new ParameterDefinition(
                Key: "emit_snapshot",
                Label: "Guardar evidencia",
                Type: ParameterType.Boolean,
                DefaultValue: true,
                Description: "Genera una captura visual al detectar el cruce.")
        ]);

    public static readonly AnalyticDefinition PersonCountingDefinition = new(
        Id: "person_counting",
        DisplayName: "Conteo de Personas",
        Description: "Mantiene conteos de entradas y salidas utilizando líneas virtuales y seguimiento de personas.",
        Category: AnalyticCategory.Operations,
        Version: "1.0.0",
        RequiredCapabilities:
        [
            InferenceCapability.ObjectDetection,
            InferenceCapability.Tracking
        ],
        Features:
        [
            AnalyticFeature.Lines,
            AnalyticFeature.Tracking,
            AnalyticFeature.Schedules
        ],
        ProducedEventTypes:
        [
            "person_count_updated",
            "occupancy_threshold_reached"
        ],
        Parameters:
        [
            new ParameterDefinition(
                Key: "confidence_threshold",
                Label: "Umbral de confianza",
                Type: ParameterType.Number,
                DefaultValue: 0.60,
                Min: 0.10,
                Max: 0.95,
                Step: 0.05,
                Description: "Umbral mínimo de confianza de detección."),
            new ParameterDefinition(
                Key: "entry_direction",
                Label: "Dirección de entrada",
                Type: ParameterType.Select,
                DefaultValue: "a_to_b",
                Options:
                [
                    new ParameterOption("a_to_b", "A -> B"),
                    new ParameterOption("b_to_a", "B -> A")
                ],
                Description: "Sentido del cruce que incrementa las entradas."),
            new ParameterDefinition(
                Key: "initial_occupancy",
                Label: "Ocupación inicial",
                Type: ParameterType.Number,
                DefaultValue: 0,
                Min: 0,
                Max: 100000,
                Step: 1,
                Description: "Valor inicial del contador de ocupación."),
            new ParameterDefinition(
                Key: "minimum_occupancy",
                Label: "Ocupación mínima",
                Type: ParameterType.Number,
                DefaultValue: 0,
                Min: 0,
                Max: 100000,
                Step: 1,
                Description: "Límite inferior para evitar conteos negativos."),
            new ParameterDefinition(
                Key: "maximum_occupancy",
                Label: "Ocupación máxima",
                Type: ParameterType.Number,
                DefaultValue: 0,
                Min: 0,
                Max: 100000,
                Step: 1,
                Description: "Umbral de capacidad que dispara alertas de aforo.")
        ]);

    private readonly Dictionary<string, AnalyticDefinition> _definitions;

    public StandardAnalyticCatalog(IEnumerable<AnalyticDefinition>? customDefinitions = null)
    {
        _definitions = new Dictionary<string, AnalyticDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [HandRaiseDefinition.Id] = HandRaiseDefinition,
            [PersonPresenceDefinition.Id] = PersonPresenceDefinition,
            [ZoneIntrusionDefinition.Id] = ZoneIntrusionDefinition,
            [LineCrossingDefinition.Id] = LineCrossingDefinition,
            [PersonCountingDefinition.Id] = PersonCountingDefinition,
            [CustomObjectDefinition.Id] = CustomObjectDefinition
        };

        if (customDefinitions != null)
        {
            foreach (var def in customDefinitions)
            {
                _definitions[def.Id] = def;
            }
        }
    }

    public IReadOnlyList<AnalyticDefinition> GetAll() => _definitions.Values.ToArray();

    public AnalyticDefinition? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _definitions.TryGetValue(id.Trim(), out var def) ? def : null;
    }

    public (bool IsValid, string? ErrorMessage) ValidateConfiguration(
        string analyticTypeId,
        IReadOnlyDictionary<string, object?>? configuration)
    {
        if (string.IsNullOrWhiteSpace(analyticTypeId))
        {
            return (false, "El tipo de analítica es obligatorio.");
        }

        var definition = GetById(analyticTypeId);
        if (definition == null)
        {
            return (false, $"El tipo de analítica '{analyticTypeId}' no está soportado en este nodo.");
        }

        if (configuration == null || configuration.Count == 0)
        {
            return (true, null);
        }

        var paramMap = definition.Parameters.ToDictionary(p => p.Key, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, rawValue) in configuration)
        {
            if (!paramMap.TryGetValue(key, out var paramDef))
            {
                return (false, $"El parámetro '{key}' no está definido para la analítica '{analyticTypeId}'.");
            }

            if (rawValue == null)
            {
                continue;
            }

            switch (paramDef.Type)
            {
                case ParameterType.Boolean:
                    if (!TryExtractBoolean(rawValue, out _))
                    {
                        return (false, $"El parámetro '{key}' debe ser un valor booleano.");
                    }
                    break;

                case ParameterType.Number:
                    if (!TryExtractNumber(rawValue, out var number))
                    {
                        return (false, $"El parámetro '{key}' debe ser un valor numérico.");
                    }
                    if (paramDef.Min.HasValue && number < paramDef.Min.Value)
                    {
                        return (false, $"El parámetro '{key}' no puede ser menor a {paramDef.Min.Value}.");
                    }
                    if (paramDef.Max.HasValue && number > paramDef.Max.Value)
                    {
                        return (false, $"El parámetro '{key}' no puede ser mayor a {paramDef.Max.Value}.");
                    }
                    break;

                case ParameterType.String:
                    if (rawValue is not string && (rawValue is not JsonElement elemStr || elemStr.ValueKind != JsonValueKind.String))
                    {
                        return (false, $"El parámetro '{key}' debe ser una cadena de texto.");
                    }
                    break;

                case ParameterType.Select:
                    var stringValue = rawValue is JsonElement jsonElem ? jsonElem.ToString() : rawValue.ToString();
                    if (paramDef.Options != null && paramDef.Options.Count > 0 &&
                        !paramDef.Options.Any(o => string.Equals(o.Value, stringValue, StringComparison.OrdinalIgnoreCase)))
                    {
                        return (false, $"El valor '{stringValue}' no es una opción válida para '{key}'.");
                    }
                    break;
            }
        }

        return (true, null);
    }

    private static bool TryExtractBoolean(object value, out bool result)
    {
        if (value is bool b)
        {
            result = b;
            return true;
        }
        if (value is JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.True) { result = true; return true; }
            if (elem.ValueKind == JsonValueKind.False) { result = false; return true; }
            if (elem.ValueKind == JsonValueKind.String && bool.TryParse(elem.GetString(), out var parsedBool))
            {
                result = parsedBool;
                return true;
            }
        }
        if (value is string str && bool.TryParse(str, out var parsed))
        {
            result = parsed;
            return true;
        }
        result = false;
        return false;
    }

    private static bool TryExtractNumber(object value, out double result)
    {
        switch (value)
        {
            case sbyte s: result = s; return true;
            case byte b: result = b; return true;
            case short s: result = s; return true;
            case ushort u: result = u; return true;
            case int i: result = i; return true;
            case uint u: result = u; return true;
            case long l: result = l; return true;
            case ulong u: result = u; return true;
            case float f: result = f; return true;
            case double d: result = d; return true;
            case decimal dec: result = (double)dec; return true;
            case JsonElement elem when elem.ValueKind == JsonValueKind.Number && elem.TryGetDouble(out var d):
                result = d;
                return true;
            case string str when double.TryParse(str, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
