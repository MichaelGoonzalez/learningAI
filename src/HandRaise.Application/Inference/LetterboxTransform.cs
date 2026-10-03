namespace HandRaise.Application.Inference;

public readonly record struct LetterboxTransform(
    int SourceWidth,
    int SourceHeight,
    int TargetWidth,
    int TargetHeight,
    float Scale,
    int PaddingLeft,
    int PaddingTop)
{
    public float ToSourceX(float x) =>
        Math.Clamp((x - PaddingLeft) / Scale, 0, SourceWidth);

    public float ToSourceY(float y) =>
        Math.Clamp((y - PaddingTop) / Scale, 0, SourceHeight);
}
