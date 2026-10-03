namespace HandRaise.Application.Inference;

public sealed class ImageFrame
{
    public ImageFrame(int width, int height, ReadOnlyMemory<byte> bgrPixels)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (bgrPixels.Length != checked(width * height * 3))
        {
            throw new ArgumentException("El buffer debe contener exactamente width * height * 3 bytes BGR.");
        }

        Width = width;
        Height = height;
        BgrPixels = bgrPixels;
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> BgrPixels { get; }
}
