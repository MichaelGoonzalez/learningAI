namespace HandRaise.Application.Tracking;

public sealed record TrackerOptions
{
    public required float HighConfidenceThreshold { get; init; }
    public required float LowConfidenceThreshold { get; init; }
    public required float NewTrackThreshold { get; init; }
    public required float FirstMatchIouThreshold { get; init; }
    public required float SecondMatchIouThreshold { get; init; }
    public required int LostTrackBufferFrames { get; init; }
    public required double NominalFramesPerSecond { get; init; }
    public required double PositionProcessNoise { get; init; }
    public required double VelocityProcessNoise { get; init; }
    public required double MeasurementNoise { get; init; }

    public void Validate()
    {
        ValidateProbability(HighConfidenceThreshold, nameof(HighConfidenceThreshold));
        ValidateProbability(LowConfidenceThreshold, nameof(LowConfidenceThreshold));
        ValidateProbability(NewTrackThreshold, nameof(NewTrackThreshold));
        ValidateProbability(FirstMatchIouThreshold, nameof(FirstMatchIouThreshold));
        ValidateProbability(SecondMatchIouThreshold, nameof(SecondMatchIouThreshold));
        if (LowConfidenceThreshold > HighConfidenceThreshold ||
            NewTrackThreshold < HighConfidenceThreshold)
        {
            throw new ArgumentException("Los umbrales de confianza del tracker no son coherentes.");
        }

        if (LostTrackBufferFrames < 0 || NominalFramesPerSecond <= 0 || PositionProcessNoise <= 0 ||
            VelocityProcessNoise <= 0 || MeasurementNoise <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LostTrackBufferFrames));
        }
    }

    private static void ValidateProbability(float value, string name)
    {
        if (value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
