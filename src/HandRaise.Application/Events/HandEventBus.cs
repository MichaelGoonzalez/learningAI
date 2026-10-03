using System.Threading.Channels;

namespace HandRaise.Application.Events;

public sealed class HandEventBus : IHandEventPublisher, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<long, Channel<HandEvent>> _subscribers = [];
    private long _nextSubscriberId;
    private bool _completed;

    public HandEventSubscription Subscribe()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            var id = _nextSubscriberId++;
            var channel = Channel.CreateUnbounded<HandEvent>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = true,
                AllowSynchronousContinuations = false
            });
            _subscribers.Add(id, channel);
            return new HandEventSubscription(channel.Reader, () => Unsubscribe(id));
        }
    }

    public async ValueTask PublishAsync(
        HandEvent handEvent,
        CancellationToken cancellationToken = default)
    {
        Channel<HandEvent>[] subscribers;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            subscribers = _subscribers.Values.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            await subscriber.Writer.WriteAsync(handEvent, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        Channel<HandEvent>[] subscribers;
        lock (_sync)
        {
            if (_completed)
            {
                return ValueTask.CompletedTask;
            }

            _completed = true;
            subscribers = _subscribers.Values.ToArray();
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }

    private void Unsubscribe(long id)
    {
        Channel<HandEvent>? channel;
        lock (_sync)
        {
            if (!_subscribers.Remove(id, out channel))
            {
                return;
            }
        }

        channel.Writer.TryComplete();
    }
}
