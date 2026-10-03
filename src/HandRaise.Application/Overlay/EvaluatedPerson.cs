using HandRaise.Application.Inference;
using HandRaise.Domain.Detection;

namespace HandRaise.Application.Overlay;

public sealed record EvaluatedPerson(
    PosePerson Detection,
    HandsState Hands,
    string? Zone,
    int? TrackId = null,
    TrackPhase? Phase = null);
