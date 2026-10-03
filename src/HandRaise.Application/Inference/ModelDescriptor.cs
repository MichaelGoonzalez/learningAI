namespace HandRaise.Application.Inference;

public sealed record ModelDescriptor(
    string Path,
    int InputWidth,
    int InputHeight,
    int ClassCount,
    int KeypointCount,
    float ConfidenceThreshold,
    float IouThreshold,
    int MaximumDetections)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        if (InputWidth <= 0 || InputHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InputWidth), "El tamaño de entrada debe ser positivo.");
        }

        if (ClassCount <= 0 || KeypointCount <= 0 || MaximumDetections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ClassCount));
        }

        if (ConfidenceThreshold is < 0 or > 1 || IouThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ConfidenceThreshold));
        }
    }
}
