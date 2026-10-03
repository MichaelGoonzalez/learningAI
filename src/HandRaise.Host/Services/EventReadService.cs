using HandRaise.Application.Events;
using HandRaise.Application.Storage;

namespace HandRaise.Host.Services;

public sealed record PagedEvents(IReadOnlyList<HandEvent> Items, int Limit, int Offset);
public sealed record SnapshotFile(string Path, string ContentType);

public sealed class EventReadService(IHandEventRepository repository, string snapshotRoot)
{
    private readonly string _snapshotRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(snapshotRoot));

    public async Task<PagedEvents> QueryAsync(EventQuery query, CancellationToken token)
    {
        query.Validate();
        return new(await repository.QueryAsync(query, token), query.Limit, query.Offset);
    }

    public Task<IReadOnlyList<EventStatistic>> StatisticsAsync(EventQuery query, CancellationToken token)
    {
        query.Validate();
        return repository.GetStatisticsAsync(query, token);
    }

    public async Task<SnapshotFile?> SnapshotAsync(string id, CancellationToken token)
    {
        var handEvent = await repository.GetByIdAsync(id, token);
        if (handEvent?.SnapshotUrl is not { Length: > 0 } relative) return null;
        var path = Path.GetFullPath(Path.Combine(_snapshotRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _snapshotRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        return new(path, "image/jpeg");
    }
}
