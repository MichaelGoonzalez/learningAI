namespace HandRaise.Domain.Detection;

public readonly record struct HandsState(
    bool LeftRaised,
    bool RightRaised,
    double Confidence)
{
    public bool AnyRaised => LeftRaised || RightRaised;

    public HandSide? Hand => (LeftRaised, RightRaised) switch
    {
        (true, true) => HandSide.Both,
        (true, false) => HandSide.Left,
        (false, true) => HandSide.Right,
        _ => null,
    };
}

