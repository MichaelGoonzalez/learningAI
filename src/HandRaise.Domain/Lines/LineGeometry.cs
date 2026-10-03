using HandRaise.Domain.Zones;

namespace HandRaise.Domain.Lines;

public enum LineSide
{
    OnLine = 0,
    SideA = 1,
    SideB = 2
}

public static class LineGeometry
{
    public const double DefaultHysteresisMargin = 0.015;

    public static double SignedDistance(Point2D p, Point2D a, Point2D b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return 0.0;
        return (dx * (p.Y - a.Y) - dy * (p.X - a.X)) / len;
    }

    public static LineSide DetermineSide(Point2D p, Point2D a, Point2D b, double margin = DefaultHysteresisMargin)
    {
        var dist = SignedDistance(p, a, b);
        if (dist > margin) return LineSide.SideA;
        if (dist < -margin) return LineSide.SideB;
        return LineSide.OnLine;
    }

    public static double ProjectionParameter(Point2D p, Point2D a, Point2D b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12) return 0.0;
        return ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
    }

    public static bool SegmentsIntersect(Point2D p1, Point2D p2, Point2D q1, Point2D q2)
    {
        var d1 = CrossProduct(q1, q2, p1);
        var d2 = CrossProduct(q1, q2, p2);
        var d3 = CrossProduct(p1, p2, q1);
        var d4 = CrossProduct(p1, p2, q2);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        if (Math.Abs(d1) < 1e-9 && OnSegment(q1, q2, p1)) return true;
        if (Math.Abs(d2) < 1e-9 && OnSegment(q1, q2, p2)) return true;
        if (Math.Abs(d3) < 1e-9 && OnSegment(p1, p2, q1)) return true;
        if (Math.Abs(d4) < 1e-9 && OnSegment(p1, p2, q2)) return true;

        return false;
    }

    private static double CrossProduct(Point2D a, Point2D b, Point2D c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool OnSegment(Point2D a, Point2D b, Point2D c) =>
        c.X >= Math.Min(a.X, b.X) - 1e-9 && c.X <= Math.Max(a.X, b.X) + 1e-9 &&
        c.Y >= Math.Min(a.Y, b.Y) - 1e-9 && c.Y <= Math.Max(a.Y, b.Y) + 1e-9;
}

