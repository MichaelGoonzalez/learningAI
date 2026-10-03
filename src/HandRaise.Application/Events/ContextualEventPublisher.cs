namespace HandRaise.Application.Events;

public sealed class ContextualEventPublisher(
    IHandEventPublisher inner,
    string nodeId,
    string siteId) : IHandEventPublisher
{
    public ValueTask PublishAsync(HandEvent handEvent, CancellationToken cancellationToken = default) =>
        inner.PublishAsync(handEvent with { NodeId = nodeId, SiteId = siteId }, cancellationToken);
}
