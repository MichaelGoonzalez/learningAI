namespace HandRaise.Application.Events;

public interface IHandEventPublisher
{
    ValueTask PublishAsync(HandEvent handEvent, CancellationToken cancellationToken = default);
}
