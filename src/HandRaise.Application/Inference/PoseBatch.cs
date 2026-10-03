namespace HandRaise.Application.Inference;

public sealed record PoseBatch(
    IReadOnlyList<PosePerson> People,
    TimeSpan PreprocessLatency,
    TimeSpan InferenceLatency,
    TimeSpan PostprocessLatency);
