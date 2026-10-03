namespace HandRaise.Application.Hardware;

public sealed record RuntimeInfo(
    bool OnnxRuntimeInstalled,
    bool CpuAvailable,
    bool DirectMlAvailable,
    bool CudaAvailable,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> MissingComponents)
{
    public static RuntimeInfo DevelopmentFallback { get; } = new(
        OnnxRuntimeInstalled: false,
        CpuAvailable: true,
        DirectMlAvailable: false,
        CudaAvailable: false,
        Providers: ["CPU (pendiente de integrar ONNX Runtime en C3)"],
        MissingComponents: ["ONNX Runtime"]);
}
