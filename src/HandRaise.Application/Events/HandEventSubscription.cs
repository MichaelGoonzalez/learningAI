using System.Threading.Channels;

namespace HandRaise.Application.Events;

public sealed class HandEventSubscription : IAsyncDisposable
{
    private readonly Action _unsubscribe;
    private bool _disposed;

    internal HandEventSubscription(ChannelReader<HandEvent> reader, Action unsubscribe)
    {
        Reader = reader;
        _unsubscribe = unsubscribe;
    }

    public ChannelReader<HandEvent> Reader { get; }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _unsubscribe();
        }

        return ValueTask.CompletedTask;
    }
}
