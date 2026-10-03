namespace HandRaise.Domain.Zones;

public static class ZoneGeometry
{
    public static bool PointInPolygon(
        Point2D point,
        IReadOnlyList<Point2D> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count < 3)
        {
            throw new ArgumentException(
                "Un polígono requiere al menos tres puntos.",
                nameof(polygon));
        }

        var inside = false;
        var previous = polygon[^1];
        foreach (var current in polygon)
        {
            if (PointOnSegment(point, previous, current))
            {
                return true;
            }

            var crossesScanline = (previous.Y > point.Y) != (current.Y > point.Y);
            if (crossesScanline)
            {
                var intersectionX = previous.X +
                    ((point.Y - previous.Y) * (current.X - previous.X) /
                     (current.Y - previous.Y));
                if (point.X < intersectionX)
                {
                    inside = !inside;
                }
            }
            previous = current;
        }
        return inside;
    }

    public static string? FindZone(
        Point2D point,
        IReadOnlyList<ZoneDefinition> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        foreach (var zone in zones)
        {
            zone.Validate();
            if (PointInPolygon(point, zone.Points))
            {
                return zone.Name;
            }
        }
        return null;
    }

    private static bool PointOnSegment(Point2D point, Point2D start, Point2D end)
    {
        var crossProduct =
            ((point.X - start.X) * (end.Y - start.Y)) -
            ((point.Y - start.Y) * (end.X - start.X));
        if (crossProduct != 0)
        {
            return false;
        }

        return point.X >= Math.Min(start.X, end.X) &&
               point.X <= Math.Max(start.X, end.X) &&
               point.Y >= Math.Min(start.Y, end.Y) &&
               point.Y <= Math.Max(start.Y, end.Y);
    }
}

