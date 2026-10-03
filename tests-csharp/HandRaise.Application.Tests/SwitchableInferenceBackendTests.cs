using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;

namespace HandRaise.Application.Tests;

public sealed class SwitchableInferenceBackendTests
{
    [Fact]
    public async Task SwitchAsync_LoadsCandidateBeforeReplacingActiveBackend()
    {
        var cpu = Device("cpu", InferenceBackend.Cpu);
        var gpu = Device("dml", InferenceBackend.DirectMl);
        var initial = new StubBackend(cpu);
        var factory = new StubFactory(device => new StubBackend(device));
        await using var backend = new SwitchableInferenceBackend(initial, factory);
        await backend.LoadAsync(Model());

        var result = await backend.SwitchAsync(gpu);

        Assert.True(result.Success);
        Assert.Equal(gpu, backend.Device);
        Assert.Equal(1, backend.Generation);
        Assert.True(initial.Disposed);
    }

    [Fact]
    public async Task SwitchAsync_WhenCandidateFails_KeepsActiveBackend()
    {
        var cpu = Device("cpu", InferenceBackend.Cpu);
        var gpu = Device("dml", InferenceBackend.DirectMl);
        var initial = new StubBackend(cpu);
        var factory = new StubFactory(device => new StubBackend(device, failLoad: true));
        await using var backend = new SwitchableInferenceBackend(initial, factory);
        await backend.LoadAsync(Model());

        var result = await backend.SwitchAsync(gpu);

        Assert.False(result.Success);
        Assert.Equal(cpu, backend.Device);
        Assert.Equal(0, backend.Generation);
        Assert.False(initial.Disposed);
    }

    private static DeviceInfo Device(string id, InferenceBackend backend) => new(
        id, id, backend == InferenceBackend.Cpu ? HardwareVendor.Cpu : HardwareVendor.Nvidia,
        backend, null, null, null, true);

    private static ModelDescriptor Model() => new("model.onnx", 1, 1, 1, 17, 0.25f, 0.7f, 10);

    private sealed class StubFactory(Func<DeviceInfo, IInferenceBackend> create) : IInferenceBackendFactory
    {
        public IInferenceBackend Create(DeviceInfo device) => create(device);
    }

    private sealed class StubBackend(DeviceInfo device, bool failLoad = false) : IInferenceBackend
    {
        public DeviceInfo Device { get; } = device;
        public InferenceExecutionInfo ExecutionInfo { get; } = new(
            "fake", device.Id, device.Name, true, "fake");
        public bool Disposed { get; private set; }

        public ValueTask LoadAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
        {
            if (failLoad)
            {
                throw new InvalidOperationException("load failed");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<PoseBatch> InferAsync(
            ImageFrame frame,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PoseBatch([], TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
