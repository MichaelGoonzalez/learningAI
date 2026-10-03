using HandRaise.Domain.Zones;

namespace HandRaise.Application.Zones;

public static class ZoneEditorLogic
{
    public static IReadOnlyList<ZoneDefinition> ToPixels(
        IReadOnlyList<NormalizedZone> zones,
        int width,
        int height)
    {
        ValidateFrame(width, height);
        Validate(zones);
        return zones.Select(zone => new ZoneDefinition(
            zone.Name,
            zone.Points.Select(point => new Point2D(point.X * width, point.Y * height)).ToArray()))
            .ToArray();
    }

    public static IReadOnlyList<NormalizedZone> ToNormalized(
        IReadOnlyList<ZoneDefinition> zones,
        int width,
        int height)
    {
        ValidateFrame(width, height);
        var normalized = zones.Select(zone => new NormalizedZone(
            zone.Name,
            zone.Points.Select(point => new NormalizedPoint(point.X / width, point.Y / height)).ToArray()))
            .ToArray();
        Validate(normalized);
        return normalized;
    }

    public static void Validate(IReadOnlyList<NormalizedZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Name))
            {
                throw new ArgumentException("La zona debe tener un nombre.");
            }
            if (!names.Add(zone.Name.Trim()))
            {
                throw new ArgumentException($"El nombre de zona '{zone.Name}' está repetido.");
            }
            if (zone.Points.Count < 3 || zone.Points.Distinct().Count() < 3)
            {
                throw new ArgumentException($"La zona '{zone.Name}' requiere al menos 3 puntos distintos.");
            }
            if (zone.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X is < 0 or > 1 || point.Y is < 0 or > 1))
            {
                throw new ArgumentException($"Los puntos de '{zone.Name}' deben estar entre 0 y 1.");
            }
            if (Math.Abs(SignedArea(zone.Points)) < 1e-10)
            {
                throw new ArgumentException($"La zona '{zone.Name}' es degenerada.");
            }
            if (HasSelfIntersection(zone.Points))
            {
                throw new ArgumentException($"La zona '{zone.Name}' se cruza consigo misma.");
            }
        }
    }

    private static bool HasSelfIntersection(IReadOnlyList<NormalizedPoint> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var iNext = (i + 1) % points.Count;
            for (var j = i + 1; j < points.Count; j++)
            {
                var jNext = (j + 1) % points.Count;
                if (i == j || iNext == j || jNext == i)
                {
                    continue;
                }
                if (SegmentsIntersect(points[i], points[iNext], points[j], points[jNext]))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool SegmentsIntersect(
        NormalizedPoint a, NormalizedPoint b, NormalizedPoint c, NormalizedPoint d)
    {
        static double Cross(NormalizedPoint p, NormalizedPoint q, NormalizedPoint r) =>
            ((q.X - p.X) * (r.Y - p.Y)) - ((q.Y - p.Y) * (r.X - p.X));
        var c1 = Cross(a, b, c);
        var c2 = Cross(a, b, d);
        var c3 = Cross(c, d, a);
        var c4 = Cross(c, d, b);
        return ((c1 > 0 && c2 < 0) || (c1 < 0 && c2 > 0)) &&
               ((c3 > 0 && c4 < 0) || (c3 < 0 && c4 > 0));
    }

    private static double SignedArea(IReadOnlyList<NormalizedPoint> points)
    {
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var next = points[(i + 1) % points.Count];
            sum += (points[i].X * next.Y) - (next.X * points[i].Y);
        }
        return sum / 2;
    }

    private static void ValidateFrame(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "El frame debe tener dimensiones positivas.");
        }
    }
}

public sealed class AtomicZoneProvider : IZoneProvider
{
    private readonly IReadOnlyList<ZoneDefinition> _legacyPixelZones;
    private NormalizedZone[]? _normalized;

    public AtomicZoneProvider(
        IReadOnlyList<ZoneDefinition> configuredZones,
        bool configuredAsNormalized = false)
    {
        if (configuredAsNormalized)
        {
            var zones = configuredZones.Select(zone => new NormalizedZone(
                zone.Name,
                zone.Points.Select(point => new NormalizedPoint(point.X, point.Y)).ToArray())).ToArray();
            ZoneEditorLogic.Validate(zones);
            _normalized = zones;
            _legacyPixelZones = [];
        }
        else
        {
            foreach (var zone in configuredZones)
            {
                zone.Validate();
            }
            _legacyPixelZones = configuredZones.ToArray();
        }
    }

    public IReadOnlyList<ZoneDefinition> GetZones(int frameWidth, int frameHeight)
    {
        var current = Volatile.Read(ref _normalized);
        return current is null
            ? _legacyPixelZones
            : ZoneEditorLogic.ToPixels(current, frameWidth, frameHeight);
    }

    public IReadOnlyList<NormalizedZone> GetNormalized(int frameWidth, int frameHeight)
    {
        var current = Volatile.Read(ref _normalized);
        return current ?? ZoneEditorLogic.ToNormalized(_legacyPixelZones, frameWidth, frameHeight);
    }

    public void Replace(IReadOnlyList<NormalizedZone> zones)
    {
        ZoneEditorLogic.Validate(zones);
        var copy = zones.Select(zone => new NormalizedZone(
            zone.Name.Trim(), zone.Points.ToArray())).ToArray();
        Volatile.Write(ref _normalized, copy);
    }
}
