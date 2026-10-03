namespace HandRaise.Application.Inference;

public sealed record InferenceExecutionInfo(
    string ProviderName,
    string DeviceId,
    string DeviceName,
    bool ExplicitlySelected,
    string Verification);
