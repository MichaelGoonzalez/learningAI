using HandRaise.Application.Events;

namespace HandRaise.Application.Storage;

public interface IHandEventRepository
{
    Task SaveBatchAsync(IReadOnlyList<HandEvent> events, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HandEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default);
    Task<HandEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EventStatistic>> GetStatisticsAsync(
        EventQuery query,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default);
    Task DeleteEventsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
    Task ClearSnapshotPathAsync(string eventId, CancellationToken cancellationToken = default);
}
