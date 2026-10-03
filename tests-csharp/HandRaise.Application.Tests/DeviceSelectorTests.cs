using HandRaise.Application.Hardware;

namespace HandRaise.Application.Tests;

public sealed class DeviceSelectorTests
{
    [Fact]
    public void RecommendDevice_WithOnlyCpu_ReturnsCpu()
    {
        var cpu = Device("cpu", HardwareVendor.Cpu, InferenceBackend.Cpu);

        Assert.Same(cpu, DeviceSelector.RecommendDevice([cpu]));
    }

    [Fact]
    public void RecommendDevice_PrefersNvidiaCudaOverDirectMl()
    {
        var cpu = Device("cpu", HardwareVendor.Cpu, InferenceBackend.Cpu);
        var amd = Device("dml-amd", HardwareVendor.Amd, InferenceBackend.DirectMl, 8192);
        var cuda = Device("cuda-nvidia", HardwareVendor.Nvidia, InferenceBackend.Cuda, 4096);

        Assert.Same(cuda, DeviceSelector.RecommendDevice([cpu, amd, cuda]));
    }

    [Fact]
    public void RecommendDevice_WhenCudaRuntimeIsMissing_UsesDirectMl()
    {
        var cpu = Device("cpu", HardwareVendor.Cpu, InferenceBackend.Cpu);
        var directMl = Device("dml", HardwareVendor.Nvidia, InferenceBackend.DirectMl);
        var cuda = Device("cuda", HardwareVendor.Nvidia, InferenceBackend.Cuda) with
        {
            RuntimeAvailable = false,
            UnavailableReason = "CUDA no instalado"
        };

        Assert.Same(directMl, DeviceSelector.RecommendDevice([cpu, cuda, directMl]));
    }

    [Fact]
    public void RecommendDevice_SelectsDirectMlGpuWithMostVram()
    {
        var integrated = Device("intel", HardwareVendor.Intel, InferenceBackend.DirectMl, 1024);
        var dedicated = Device("amd", HardwareVendor.Amd, InferenceBackend.DirectMl, 8192);

        Assert.Same(dedicated, DeviceSelector.RecommendDevice([integrated, dedicated]));
    }

    [Fact]
    public void ResolveSavedDevice_WhenItDisappeared_FallsBackToCpu()
    {
        var cpu = Device("cpu", HardwareVendor.Cpu, InferenceBackend.Cpu);

        var result = DeviceSelector.ResolveSavedDevice([cpu], "cuda:removed");

        Assert.Same(cpu, result.Device);
        Assert.True(result.UsedFallback);
        Assert.Contains("ya no existe", result.Warning);
    }

    [Fact]
    public void ResolveSavedDevice_WhenRuntimeBroke_FallsBackToCpu()
    {
        var cpu = Device("cpu", HardwareVendor.Cpu, InferenceBackend.Cpu);
        var gpu = Device("dml", HardwareVendor.Amd, InferenceBackend.DirectMl) with
        {
            RuntimeAvailable = false,
            UnavailableReason = "driver roto"
        };

        var result = DeviceSelector.ResolveSavedDevice([cpu, gpu], gpu.Id);

        Assert.Same(cpu, result.Device);
        Assert.True(result.UsedFallback);
        Assert.Contains("driver roto", result.Warning);
    }

    private static DeviceInfo Device(
        string id,
        HardwareVendor vendor,
        InferenceBackend backend,
        long? vramMb = null) =>
        new(id, id, vendor, backend, null, vramMb, null, true);
}
