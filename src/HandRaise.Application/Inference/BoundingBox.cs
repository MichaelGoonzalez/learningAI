namespace HandRaise.Application.Inference;

public readonly record struct BoundingBox(float Left, float Top, float Right, float Bottom)
{
    public float Width => Math.Max(0, Right - Left);

    public float Height => Math.Max(0, Bottom - Top);

    public float Area => Width * Height;
}
