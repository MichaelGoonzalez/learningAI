using System.Threading.Channels;
using HandRaise.Application.Capture;
using HandRaise.Application.Events;
using HandRaise.Application.Storage;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class EventPersistenceWorker : IAsyncDisposable
{
    private readonly HandEventSubscription _subscription;
    private readonly IHandEventRepository _repository;
    private readonly SnapshotStore _snapshots;
    private readonly StorageWorkerOptions _options;
    private readonly RetentionService _retention;
    private readonly Action<string>? _log;
    private readonly Channel<HandEvent> _queue;
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private readonly Task _pump;
    private readonly Task _writer;
    private readonly CancellationTokenSource _cleanupCancellation = new();
    private readonly Task _cleanup;
    private bool _disposed;
    private long _dropped;

    public EventPersistenceWorker(
        HandEventBus bus,
        IHandEventRepository repository,
        SnapshotStore snapshots,
        StorageWorkerOptions options,
        IUtcClock? clock = null,
        Action<string>? log = null)
    {
        options.Validate();
        _subscription = bus.Subscribe();
        _repository = repository;
        _snapshots = snapshots;
        _options = options;
        _retention = new RetentionService(
            repository, snapshots, options, clock ?? SystemUtcClock.Instance);
        _log = log;
        _queue = Channel.CreateBounded<HandEvent>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _pump = PumpAsync();
        _writer = PersistAsync();
        _cleanup = CleanupLoopAsync(_cleanupCancellation.Token);
    }

    public long DroppedEvents => Interlocked.Read(ref _dropped);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _subscription.DisposeAsync();
        await _pump;
        await _writer;
        _cleanupCancellation.Cancel();
        await _cleanup;
        await RunCleanupSafelyAsync();
        _cleanupCancellation.Dispose();
        _storageGate.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var handEvent in _subscription.Reader.ReadAllAsync())
            {
                if (!_queue.Writer.TryWrite(handEvent))
                {
                    Interlocked.Increment(ref _dropped);
                    _log?.Invoke($"Persistencia saturada; evento descartado: {handEvent.Id}");
                }
            }
        }
        catch (Exception exception)
        {
            _log?.Invoke($"El suscriptor de persistencia falló: {exception.Message}");
        }
        finally
        {
            _queue.Writer.TryComplete();
        }
    }

    private async Task PersistAsync()
    {
        var batch = new List<HandEvent>(_options.BatchSize);
        await foreach (var first in _queue.Reader.ReadAllAsync())
        {
            batch.Add(first);
            while (batch.Count < _options.BatchSize && _queue.Reader.TryRead(out var next))
            {
                batch.Add(next);
            }

            await PersistBatchSafelyAsync(batch);
            batch.Clear();
        }
    }

    private async Task PersistBatchSafelyAsync(List<HandEvent> batch)
    {
        await _storageGate.WaitAsync();
        try
        {
            var prepared = new List<HandEvent>(batch.Count);
            foreach (var handEvent in batch)
            {
                var item = handEvent;
                if (handEvent.SnapshotJpeg is { Length: > 0 } jpeg)
                {
                    try
                    {
                        var relative = await _snapshots.SaveAsync(
                            handEvent.Id, handEvent.Timestamp, jpeg);
                        item = handEvent with { SnapshotUrl = relative, SnapshotJpeg = null };
                    }
                    catch (Exception exception)
                    {
                        _log?.Invoke($"No se guardó el snapshot {handEvent.Id}: {exception.Message}");
                        item = handEvent with { SnapshotJpeg = null };
                    }
                }

                prepared.Add(item);
            }

            try
            {
                await _repository.SaveBatchAsync(prepared);
            }
            catch (Exception exception)
            {
                _log?.Invoke($"No se guardó un lote de {prepared.Count} eventos: {exception.Message}");
            }
        }
        finally
        {
            _storageGate.Release();
        }
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        await RunCleanupSafelyAsync();
        using var timer = new PeriodicTimer(_options.CleanupInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RunCleanupSafelyAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunCleanupSafelyAsync()
    {
        await _storageGate.WaitAsync();
        try
        {
            try
            {
                await _retention.RunOnceAsync();
            }
            catch (Exception exception)
            {
                _log?.Invoke($"La limpieza de retención falló: {exception.Message}");
            }
        }
        finally
        {
            _storageGate.Release();
        }
    }
}
