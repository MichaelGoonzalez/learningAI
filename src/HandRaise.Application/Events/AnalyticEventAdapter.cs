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

        var metadata = genericEvent.Metadata != null
            ? new Dictionary<string, object?>(genericEvent.Metadata)
            : new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(genericEvent.AnalyticInstanceId))
        {
            metadata["analytic_instance_id"] = genericEvent.AnalyticInstanceId;
        }

        if (!string.IsNullOrWhiteSpace(genericEvent.AnalyticType))
        {
            metadata["analytic_type"] = genericEvent.AnalyticType;
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
            Metadata: metadata)
        {
            SnapshotJpeg = genericEvent.SnapshotJpeg
        };
    }

    public static AnalyticEvent FromLegacyHandEvent(HandEvent handEvent, string? analyticInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(handEvent);

        var metadata = handEvent.Metadata != null
            ? new Dictionary<string, object?>(handEvent.Metadata)
            : new Dictionary<string, object?>();

        if (!metadata.ContainsKey("hand") && !string.IsNullOrEmpty(handEvent.Hand))
        {
            metadata["hand"] = handEvent.Hand;
        }

        var instanceId = analyticInstanceId;
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            if (metadata.TryGetValue("analytic_instance_id", out var instVal) && instVal != null)
            {
                instanceId = instVal.ToString();
            }
        }
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            instanceId = "legacy_hand_raise";
        }

        var analyticType = "hand_raise";
        if (metadata.TryGetValue("analytic_type", out var typeVal) && typeVal != null && !string.IsNullOrWhiteSpace(typeVal.ToString()))
        {
            analyticType = typeVal.ToString()!;
        }

        return new AnalyticEvent(
            Id: handEvent.Id,
            CameraId: handEvent.CameraId,
            AnalyticInstanceId: instanceId!,
            AnalyticType: analyticType,
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
