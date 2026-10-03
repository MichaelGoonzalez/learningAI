namespace HandRaise.Application.Storage;

public sealed record EventQuery(
    string? CameraId = null,
    string? Zone = null,
    string? Type = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 100,
    int Offset = 0)
{
    public void Validate()
    {
        if (Limit is < 1 or > 1000 || Offset < 0 || From > To)
        {
            throw new ArgumentException("Filtros o paginación de eventos no válidos.");
        }
    }
}
