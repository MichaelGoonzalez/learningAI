namespace HandRaise.Domain.Zones;

public sealed record ZoneDefinition(string Name, IReadOnlyList<Point2D> Points)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("La zona debe tener nombre.", nameof(Name));
        }
        if (Points.Count < 3 || Points.Distinct().Count() < 3)
        {
            throw new ArgumentException(
                "Una zona requiere al menos tres puntos distintos.",
                nameof(Points));
        }
    }
}

