using HandRaise.Application.Events;
using HandRaise.Application.Storage;
using Microsoft.EntityFrameworkCore;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class SqliteHandEventRepository(
    DbContextOptions<HandRaiseDbContext> contextOptions) : IHandEventRepository
{
    public async Task SaveBatchAsync(
        IReadOnlyList<HandEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        await using var context = new HandRaiseDbContext(contextOptions);
        context.Events.AddRange(events.Select(ToEntity));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HandEvent>> QueryAsync(
        EventQuery query,
        CancellationToken cancellationToken = default)
    {
        query.Validate();
        await using var context = new HandRaiseDbContext(contextOptions);
        var entities = await ApplyFilters(context.Events.AsNoTracking(), query)
            .OrderByDescending(item => item.Timestamp)
            .ThenBy(item => item.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArrayAsync(cancellationToken);
        return entities.Select(ToEvent).ToArray();
    }

    public async Task<HandEvent?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var context = new HandRaiseDbContext(contextOptions);
        var entity = await context.Events.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : ToEvent(entity);
    }

    public async Task<IReadOnlyList<EventStatistic>> GetStatisticsAsync(
        EventQuery query,
        CancellationToken cancellationToken = default)
    {
        query.Validate();
        await using var context = new HandRaiseDbContext(contextOptions);
        var values = await ApplyFilters(context.Events.AsNoTracking(), query)
            .Select(item => new { item.CameraId, item.Zone, item.Timestamp })
            .ToArrayAsync(cancellationToken);
        return values
            .GroupBy(item => new
            {
                item.CameraId,
                item.Zone,
                Hour = new DateTimeOffset(
                    item.Timestamp.Year,
                    item.Timestamp.Month,
                    item.Timestamp.Day,
                    item.Timestamp.Hour,
                    0,
                    0,
                    TimeSpan.Zero)
            })
            .Select(group => new EventStatistic(
                group.Key.CameraId, group.Key.Zone, group.Key.Hour, group.LongCount()))
            .OrderBy(item => item.HourUtc)
            .ThenBy(item => item.CameraId)
            .ThenBy(item => item.Zone)
            .ToArray();
    }

    public async Task<IReadOnlyList<StoredSnapshot>> GetSnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = new HandRaiseDbContext(contextOptions);
        return await context.Events.AsNoTracking()
            .Where(item => item.SnapshotPath != null)
            .OrderBy(item => item.Timestamp)
            .Select(item => new StoredSnapshot(item.Id, item.SnapshotPath!, item.Timestamp))
            .ToArrayAsync(cancellationToken);
    }

    public async Task DeleteEventsBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var context = new HandRaiseDbContext(contextOptions);
        await context.Events.Where(item => item.Timestamp < cutoff.ToUniversalTime())
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ClearSnapshotPathAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        await using var context = new HandRaiseDbContext(contextOptions);
        await context.Events.Where(item => item.Id == eventId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.SnapshotPath, (string?)null),
                cancellationToken);
    }

    private static IQueryable<HandEventEntity> ApplyFilters(
        IQueryable<HandEventEntity> queryable,
        EventQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.CameraId))
        {
            queryable = queryable.Where(item => item.CameraId == query.CameraId);
        }
        if (query.Zone is not null)
        {
            queryable = queryable.Where(item => item.Zone == query.Zone);
        }
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            queryable = queryable.Where(item => item.Type == query.Type);
        }
        if (query.From is { } from)
        {
            var value = from.ToUniversalTime();
            queryable = queryable.Where(item => item.Timestamp >= value);
        }
        if (query.To is { } to)
        {
            var value = to.ToUniversalTime();
            queryable = queryable.Where(item => item.Timestamp <= value);
        }

        return queryable;
    }

    private static HandEventEntity ToEntity(HandEvent item) => new()
    {
        Id = item.Id,
        Type = item.Type,
        CameraId = item.CameraId,
        NodeId = item.NodeId,
        SiteId = item.SiteId,
        TrackId = item.TrackId,
        Hand = item.Hand,
        Zone = item.Zone,
        Confidence = item.Confidence,
        Timestamp = item.Timestamp.ToUniversalTime(),
        SnapshotPath = item.SnapshotUrl
    };

    private static HandEvent ToEvent(HandEventEntity item) => new(
        item.Id,
        item.Type,
        item.CameraId,
        item.TrackId,
        item.Hand,
        item.Zone,
        item.Confidence,
        item.Timestamp.ToUniversalTime(),
        item.SnapshotPath,
        item.NodeId,
        item.SiteId);
}
