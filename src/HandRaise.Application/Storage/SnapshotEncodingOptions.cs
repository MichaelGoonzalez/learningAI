namespace HandRaise.Application.Storage;

public sealed record SnapshotEncodingOptions(double MarginRatio, int JpegQuality)
{
    public void Validate()
    {
        if (MarginRatio < 0 || JpegQuality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MarginRatio));
        }
    }
}
