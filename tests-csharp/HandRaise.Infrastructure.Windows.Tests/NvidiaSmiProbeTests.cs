using HandRaise.Infrastructure.Windows.Hardware;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class NvidiaSmiProbeTests
{
    [Fact]
    public void Parse_ReadsAllExpectedFields()
    {
        const string output = "0, GPU-abcd, NVIDIA RTX 4070, 12282, 591.44\r\n";

        var gpu = Assert.Single(NvidiaSmiProbe.Parse(output));

        Assert.Equal(0, gpu.Index);
        Assert.Equal("GPU-abcd", gpu.Uuid);
        Assert.Equal("NVIDIA RTX 4070", gpu.Name);
        Assert.Equal(12282, gpu.VramMb);
        Assert.Equal("591.44", gpu.DriverVersion);
    }

    [Fact]
    public void Parse_IgnoresMalformedLines()
    {
        Assert.Empty(NvidiaSmiProbe.Parse("header\nnot,a,valid,line"));
    }

    [Fact]
    public async Task DetectAsync_WhenExecutableDoesNotExist_ReturnsWarning()
    {
        var probe = new NvidiaSmiProbe(
            TimeSpan.FromMilliseconds(100),
            $"missing-nvidia-smi-{Guid.NewGuid():N}.exe");

        var (devices, warning) = await probe.DetectAsync(CancellationToken.None);

        Assert.Empty(devices);
        Assert.NotNull(warning);
    }
}
