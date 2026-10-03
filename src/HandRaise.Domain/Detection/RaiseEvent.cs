namespace HandRaise.Domain.Detection;

public enum RaiseEventType
{
    HandRaised,
    HandLowered,
}

public sealed record RaiseEvent(
    RaiseEventType Type,
    int TrackId,
    HandSide Hand,
    double Confidence,
    DateTimeOffset Timestamp);

