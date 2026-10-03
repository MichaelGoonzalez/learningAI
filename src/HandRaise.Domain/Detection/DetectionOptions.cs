namespace HandRaise.Domain.Detection;

/// <summary>Umbrales de negocio; todos deben provenir de configuración.</summary>
public sealed record DetectionOptions
{
    public required double KeypointConfidence { get; init; }
    public double? ShoulderMarginPixels { get; init; }
    public double? ShoulderMarginRatio { get; init; }
    public required bool StrictMode { get; init; }
    public required double StrictMarginPixels { get; init; }
    public required int ConsecutiveFrames { get; init; }
    public TimeSpan? HoldTime { get; init; }
    public required int LowerConsecutiveFrames { get; init; }
    public required TimeSpan Cooldown { get; init; }
    public required TimeSpan TrackTimeToLive { get; init; }

    public void Validate()
    {
        if (KeypointConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(KeypointConfidence),
                "La confianza debe estar entre cero y uno.");
        }

        var configuredMargins =
            (ShoulderMarginPixels.HasValue ? 1 : 0) +
            (ShoulderMarginRatio.HasValue ? 1 : 0);
        if (configuredMargins != 1)
        {
            throw new ArgumentException(
                "Debe configurarse exactamente un margen: píxeles o proporción.");
        }

        if (ShoulderMarginPixels < 0 || ShoulderMarginRatio < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ShoulderMarginPixels),
                "El margen de hombro no puede ser negativo.");
        }

        if (StrictMarginPixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(StrictMarginPixels));
        }

        if (ConsecutiveFrames <= 0 || LowerConsecutiveFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConsecutiveFrames),
                "Los contadores de frames deben ser positivos.");
        }

        if (HoldTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(HoldTime));
        }

        if (Cooldown < TimeSpan.Zero || TrackTimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Cooldown),
                "Los tiempos configurados no son válidos.");
        }
    }
}

