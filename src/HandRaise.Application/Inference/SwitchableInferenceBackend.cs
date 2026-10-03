using System.Diagnostics;
using HandRaise.Application.Hardware;

namespace HandRaise.Application.Inference;

public sealed class SwitchableInferenceBackend : IInferenceBackend
{
    private readonly IInferenceBackendFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IInferenceBackend _active;
    private ModelDescriptor? _model;
    private bool _disposed;

    public SwitchableInferenceBackend(IInferenceBackend initial, IInferenceBackendFactory factory)
    {
        _active = initial ?? throw new ArgumentNullException(nameof(initial));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public DeviceInfo Device => _active.Device;

    public InferenceExecutionInfo ExecutionInfo => _active.ExecutionInfo;

    public long Generation { get; private set; }

    public async ValueTask LoadAsync(ModelDescriptor model, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _active.LoadAsync(model, cancellationToken);
            _model = model;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PoseBatch> InferAsync(
        ImageFrame frame,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await _active.InferAsync(frame, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BackendSwitchResult> SwitchAsync(
        DeviceInfo device,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var model = _model ?? throw new InvalidOperationException("Debe cargarse el modelo antes de cambiar de dispositivo.");
        var stopwatch = Stopwatch.StartNew();
        IInferenceBackend? candidate = null;
        try
        {
            candidate = _factory.Create(device);
            await candidate.LoadAsync(model, cancellationToken);

            await _gate.WaitAsync(cancellationToken);
            IInferenceBackend previous;
            try
            {
                previous = _active;
                _active = candidate;
                Generation++;
                candidate = null;
            }
            finally
            {
                _gate.Release();
            }

            await previous.DisposeAsync();
            stopwatch.Stop();
            return new BackendSwitchResult(true, _active.Device, stopwatch.Elapsed, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            if (candidate is not null)
            {
                await candidate.DisposeAsync();
            }

            return new BackendSwitchResult(false, _active.Device, stopwatch.Elapsed, exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync();
        try
        {
            await _active.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
