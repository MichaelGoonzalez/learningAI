using System.Text.Json;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Infrastructure.Windows.Hardware;
using HandRaise.Infrastructure.Windows.Inference;

var arguments = ParseArguments(args);
var modelPath = Required(arguments, "model");
var framePath = Required(arguments, "bgr");
var width = ParseInt(arguments, "width");
var height = ParseInt(arguments, "height");
var confidence = ParseFloat(arguments, "confidence", 0.25f);
var iou = ParseFloat(arguments, "iou", 0.7f);
var tolerance = ParseFloat(arguments, "tolerance", 3f);

var frame = new ImageFrame(width, height, await File.ReadAllBytesAsync(framePath));
var model = new ModelDescriptor(
    Path.GetFullPath(modelPath),
    InputWidth: 640,
    InputHeight: 640,
    ClassCount: 1,
    KeypointCount: 17,
    ConfidenceThreshold: confidence,
    IouThreshold: iou,
    MaximumDetections: 300);

var inventory = await new WindowsDeviceDetector(TimeSpan.FromSeconds(3)).DetectAsync();
var cpu = inventory.Devices.Single(device => device.Backend == InferenceBackend.Cpu);
var directMl = inventory.Devices.FirstOrDefault(device =>
    device.Backend == InferenceBackend.DirectMl && device.RuntimeAvailable);

var cpuResult = await RunAsync(cpu, model, frame);
PoseBatch? directMlResult = null;
if (directMl is not null)
{
    directMlResult = await RunAsync(directMl, model, frame);
}

var comparison = Compare(cpuResult, directMlResult, tolerance);
Console.WriteLine(JsonSerializer.Serialize(new
{
    model = model.Path,
    cpu = Summary(cpu, cpuResult),
    direct_ml = directMl is null || directMlResult is null ? null : Summary(directMl, directMlResult),
    comparison
}, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));

return comparison.WithinTolerance ? 0 : 2;

static async Task<PoseBatch> RunAsync(DeviceInfo device, ModelDescriptor model, ImageFrame frame)
{
    await using var backend = new WindowsMlOnnxBackend(device);
    await backend.LoadAsync(model);
    return await backend.InferAsync(frame);
}

static object Summary(DeviceInfo device, PoseBatch result) => new
{
    device.Id,
    device.Name,
    latency_ms = result.InferenceLatency.TotalMilliseconds,
    people = result.People.Count,
    confidences = result.People.Select(person => person.Confidence).ToArray()
};

static Comparison Compare(PoseBatch cpu, PoseBatch? directMl, float tolerance)
{
    if (directMl is null)
    {
        return new Comparison(false, null, "No hay un dispositivo DirectML disponible.");
    }

    if (cpu.People.Count != directMl.People.Count)
    {
        return new Comparison(
            false,
            null,
            $"CPU detectó {cpu.People.Count} personas y DirectML {directMl.People.Count}.");
    }

    var maximumDelta = 0d;
    for (var personIndex = 0; personIndex < cpu.People.Count; personIndex++)
    {
        var cpuKeypoints = cpu.People[personIndex].Keypoints;
        var gpuKeypoints = directMl.People[personIndex].Keypoints;
        for (var keypointIndex = 0; keypointIndex < cpuKeypoints.Count; keypointIndex++)
        {
            maximumDelta = Math.Max(maximumDelta, Math.Abs(cpuKeypoints[keypointIndex].X - gpuKeypoints[keypointIndex].X));
            maximumDelta = Math.Max(maximumDelta, Math.Abs(cpuKeypoints[keypointIndex].Y - gpuKeypoints[keypointIndex].Y));
        }
    }

    return new Comparison(
        maximumDelta <= tolerance,
        maximumDelta,
        maximumDelta <= tolerance ? null : $"La diferencia supera {tolerance} píxeles.");
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= values.Length)
        {
            throw new ArgumentException("Los argumentos deben usar el formato --nombre valor.");
        }

        parsed[values[index][2..]] = values[index + 1];
    }

    return parsed;
}

static string Required(IReadOnlyDictionary<string, string> values, string name) =>
    values.TryGetValue(name, out var value)
        ? value
        : throw new ArgumentException($"Falta --{name}.");

static int ParseInt(IReadOnlyDictionary<string, string> values, string name) =>
    int.Parse(Required(values, name), System.Globalization.CultureInfo.InvariantCulture);

static float ParseFloat(
    IReadOnlyDictionary<string, string> values,
    string name,
    float defaultValue) => values.TryGetValue(name, out var value)
    ? float.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
    : defaultValue;

internal sealed record Comparison(bool WithinTolerance, double? MaximumKeypointDeltaPixels, string? Error);
