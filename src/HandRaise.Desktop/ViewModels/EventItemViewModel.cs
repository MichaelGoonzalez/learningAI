using System.Windows.Media.Imaging;
using System.IO;
using HandRaise.Application.Events;

namespace HandRaise.Desktop.ViewModels;

public sealed class EventItemViewModel
{
    private EventItemViewModel(HandEvent handEvent, BitmapSource? thumbnail)
    {
        Id = handEvent.Id;
        Timestamp = handEvent.Timestamp.LocalDateTime;
        CameraId = handEvent.CameraId;
        Type = handEvent.Type;
        Hand = handEvent.Hand;
        Zone = handEvent.Zone ?? "Sin zona";
        Thumbnail = thumbnail;
        ObjectClass = handEvent.Metadata?.GetValueOrDefault("class_name")?.ToString();
        ObjectModel = handEvent.Metadata?.GetValueOrDefault("model_name")?.ToString();
        AnalyticInstanceId = handEvent.Metadata?.GetValueOrDefault("analytic_instance_id")?.ToString();
        Confidence = handEvent.Confidence;
    }

    public string Id { get; }
    public string? ObjectClass { get; }
    public string? ObjectModel { get; }
    public string? AnalyticInstanceId { get; }
    public double Confidence { get; }
    public DateTime Timestamp { get; }
    public string CameraId { get; }
    public string Type { get; }
    public string Hand { get; }
    public string Zone { get; }
    public BitmapSource? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail != null;
    public string RawEventType => Type;
    public string TimestampText => $"{Timestamp:HH:mm:ss}";
    public string Description => $"{Title} ({Zone})";
    public string ZoneName => Zone;
    public string Title => Type switch
    {
        "custom_object_detected" => $"Objeto detectado: {ObjectClass}",
        "hand_raised" => "Mano levantada detectada",
        "hand_lowered" => "Mano bajada",
        "person_presence_started" => "Persona detectada",
        "person_presence_ended" => "Persona retirada",
        "zone_intrusion_started" => "Intrusión en zona",
        "zone_intrusion_ended" => "Fin de intrusión",
        "line_crossed" => "Cruce de línea",
        "person_count_updated" => "Actualización de conteo",
        "occupancy_threshold_reached" => "Límite de aforo alcanzado",
        _ => Type.Replace('_', ' ')
    };
    public string Detail => Type == "custom_object_detected" ? $"{Timestamp:dd/MM HH:mm:ss} · {CameraId} · {ObjectModel} · {ObjectClass} · {Confidence:P0}"
        : $"{Timestamp:dd/MM HH:mm:ss} · {CameraId} · {Hand} · {Zone}";

    public static EventItemViewModel Create(HandEvent handEvent, string snapshotRoot)
    {
        BitmapSource? image = null;
        try
        {
            if (handEvent.SnapshotJpeg is { Length: > 0 } bytes)
            {
                image = Decode(bytes);
            }
            else if (!string.IsNullOrWhiteSpace(handEvent.SnapshotUrl))
            {
                var path = Path.GetFullPath(Path.Combine(
                    Environment.ExpandEnvironmentVariables(snapshotRoot),
                    handEvent.SnapshotUrl.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(path))
                {
                    image = Decode(File.ReadAllBytes(path));
                }
            }
        }
        catch
        {
            image = null;
        }

        return new EventItemViewModel(handEvent, image);
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 120;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
