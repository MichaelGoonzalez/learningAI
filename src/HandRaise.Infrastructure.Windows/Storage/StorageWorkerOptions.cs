namespace HandRaise.Infrastructure.Windows.Storage;

public sealed record StorageWorkerOptions(
    int QueueCapacity,
    int BatchSize,
    string SnapshotDirectory,
    int? RetentionDays,
    long? MaximumSnapshotBytes,
    TimeSpan CleanupInterval)
{
    public void Validate()
    {
        if (QueueCapacity <= 0 || BatchSize <= 0 ||
            RetentionDays <= 0 || MaximumSnapshotBytes <= 0 || CleanupInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        }
    }
}
