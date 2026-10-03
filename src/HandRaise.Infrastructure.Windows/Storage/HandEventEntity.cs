namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class HandEventEntity
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public required string CameraId { get; set; }
    public required string NodeId { get; set; }
    public required string SiteId { get; set; }
    public int TrackId { get; set; }
    public required string Hand { get; set; }
    public string? Zone { get; set; }
    public double Confidence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? SnapshotPath { get; set; }
}
