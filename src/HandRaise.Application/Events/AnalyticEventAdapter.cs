using HandRaise.Domain.Analytics;

namespace HandRaise.Application.Events;

public static class AnalyticEventAdapter
{
    public static HandEvent ToLegacyHandEvent(AnalyticEvent genericEvent)
    {
        ArgumentNullException.ThrowIfNull(genericEvent);

        var hand = "unknown";
        if (genericEvent.Metadata != null && genericEvent.Metadata.TryGetValue("hand", out var value))
        {
            hand = value?.ToString() ?? "unknown";
        }

        return new HandEvent(
            Id: genericEvent.Id,
            Type: genericEvent.EventType,
            CameraId: genericEvent.CameraId,
            TrackId: genericEvent.TrackId ?? 0,
            Hand: hand,
            Zone: genericEvent.ZoneId,
            Confidence: genericEvent.Confidence ?? 0.0,
            Timestamp: genericEvent.TimestampUtc,
            SnapshotUrl: genericEvent.SnapshotUrl,
            NodeId: genericEvent.NodeId,
            SiteId: genericEvent.SiteId,
            Metadata: genericEvent.Metadata)
        {
            SnapshotJpeg = genericEvent.SnapshotJpeg
        };
    }

    public static AnalyticEvent FromLegacyHandEvent(HandEvent handEvent, string analyticInstanceId = "legacy_hand_raise")
    {
        ArgumentNullException.ThrowIfNull(handEvent);

        var metadata = handEvent.Metadata != null
            ? new Dictionary<string, object?>(handEvent.Metadata)
            : new Dictionary<string, object?>();

        if (!metadata.ContainsKey("hand") && !string.IsNullOrEmpty(handEvent.Hand))
        {
            metadata["hand"] = handEvent.Hand;
        }

        return new AnalyticEvent(
            Id: handEvent.Id,
            CameraId: handEvent.CameraId,
            AnalyticInstanceId: analyticInstanceId,
            AnalyticType: "hand_raise",
            EventType: handEvent.Type,
            TimestampUtc: handEvent.Timestamp,
            TrackId: handEvent.TrackId,
            ZoneId: handEvent.Zone,
            Confidence: handEvent.Confidence,
            Metadata: metadata,
            SnapshotUrl: handEvent.SnapshotUrl,
            NodeId: handEvent.NodeId,
            SiteId: handEvent.SiteId)
        {
            SnapshotJpeg = handEvent.SnapshotJpeg
        };
    }
}
