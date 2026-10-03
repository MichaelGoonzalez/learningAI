using HandRaise.Application.Hardware;
using Microsoft.ML.OnnxRuntime;

namespace HandRaise.Infrastructure.Windows.Hardware;

public sealed class RuntimeProbe
{
    public RuntimeInfo Detect()
    {
        var providers = new List<string>();
        var missing = new List<string>();
        var installed = false;
        var directMl = false;
        var cuda = false;

        try
        {
            var environment = OrtEnv.Instance();
            installed = true;
            foreach (var provider in environment.GetEpDevices()
                         .Select(device => device.EpName)
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                providers.Add(provider);
                directMl |= provider.Contains("DML", StringComparison.OrdinalIgnoreCase) ||
                    provider.Contains("DirectML", StringComparison.OrdinalIgnoreCase);
                cuda |= provider.Contains("CUDA", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or TypeInitializationException or OnnxRuntimeException)
        {
            missing.Add($"ONNX Runtime: {exception.GetBaseException().Message}");
        }

        if (!directMl)
        {
            missing.Add("DirectML Execution Provider");
        }

        if (!cuda)
        {
            missing.Add("CUDA Execution Provider");
        }

        return new RuntimeInfo(
            installed,
            CpuAvailable: true,
            DirectMlAvailable: directMl,
            CudaAvailable: cuda,
            providers,
            missing);
    }
}
