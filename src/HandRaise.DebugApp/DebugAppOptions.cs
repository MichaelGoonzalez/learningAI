namespace HandRaise.DebugApp;

internal sealed record DebugAppOptions(
    string? Source,
    string? DeviceId,
    bool NoWindow,
    string? RecordPath,
    string? RecordRawDirectory,
    double? MaximumSeconds,
    string? MetricsJsonPath,
    string? EventsJsonlPath,
    bool Benchmark,
    bool Persist,
    bool ListEvents,
    string? CameraFilter,
    DateTimeOffset? From,
    DateTimeOffset? To)
{
    public static DebugAppOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Argumento inesperado: {argument}");
            }

            var name = argument[2..];
            if (name is "no-window" or "benchmark" or "persist" or "list-events")
            {
                values[name] = null;
                continue;
            }

            if (++index >= args.Length)
            {
                throw new ArgumentException($"Falta el valor de --{name}.");
            }

            values[name] = args[index];
        }

        var listEvents = values.ContainsKey("list-events");
        values.TryGetValue("source", out var source);
        if (!listEvents && string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Debe indicar --source <ruta|índice|rtsp://...>.");
        }

        double? maximumSeconds = values.TryGetValue("max-seconds", out var seconds)
            ? double.Parse(seconds!, System.Globalization.CultureInfo.InvariantCulture)
            : null;
        if (maximumSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException("max-seconds");
        }

        return new DebugAppOptions(
            source,
            values.GetValueOrDefault("device"),
            values.ContainsKey("no-window"),
            values.GetValueOrDefault("record"),
            values.GetValueOrDefault("record-raw"),
            maximumSeconds,
            values.GetValueOrDefault("metrics-json"),
            values.GetValueOrDefault("events-jsonl"),
            values.ContainsKey("benchmark"),
            values.ContainsKey("persist"),
            listEvents,
            values.GetValueOrDefault("camera"),
            ParseTimestamp(values.GetValueOrDefault("from"), "from"),
            ParseTimestamp(values.GetValueOrDefault("to"), "to"));
    }

    private static DateTimeOffset? ParseTimestamp(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new ArgumentException($"--{name} debe ser una fecha ISO-8601.");
        }

        return timestamp;
    }
}
