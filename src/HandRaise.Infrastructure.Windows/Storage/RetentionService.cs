using HandRaise.Application.Capture;
using HandRaise.Application.Storage;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class RetentionService(
    IHandEventRepository repository,
    SnapshotStore snapshots,
    StorageWorkerOptions options,
    IUtcClock utcClock)
{
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        var records = await repository.GetSnapshotsAsync(cancellationToken);
        if (options.RetentionDays is { } days)
        {
            var cutoff = utcClock.UtcNow.ToUniversalTime().AddDays(-days);
            foreach (var snapshot in records.Where(item => item.Timestamp < cutoff))
            {
                snapshots.Delete(snapshot.RelativePath);
            }

            await repository.DeleteEventsBeforeAsync(cutoff, cancellationToken);
            records = await repository.GetSnapshotsAsync(cancellationToken);
        }

        if (options.MaximumSnapshotBytes is { } maximumBytes)
        {
            var sizes = records.Select(item => (Snapshot: item, Length: snapshots.GetLength(item.RelativePath)))
                .ToArray();
            var total = sizes.Sum(item => item.Length);
            foreach (var item in sizes)
            {
                if (total <= maximumBytes)
                {
                    break;
                }

                snapshots.Delete(item.Snapshot.RelativePath);
                await repository.ClearSnapshotPathAsync(item.Snapshot.EventId, cancellationToken);
                total -= item.Length;
            }
        }

        var referenced = (await repository.GetSnapshotsAsync(cancellationToken))
            .Select(item => item.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in snapshots.EnumerateRelativeJpegs()
            .Where(path => !referenced.Contains(path)))
        {
            snapshots.Delete(relativePath);
        }
    }
}
