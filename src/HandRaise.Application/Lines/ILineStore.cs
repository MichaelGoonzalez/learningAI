using HandRaise.Domain.Lines;

namespace HandRaise.Application.Lines;

public interface ILineProvider
{
    IReadOnlyList<LineDefinition> GetLines(string cameraId);
}

public interface ILineStore : ILineProvider
{
    Task<IReadOnlyList<LineDefinition>> ListByCameraAsync(string cameraId, CancellationToken cancellationToken = default);
    Task<LineDefinition?> GetByIdAsync(string cameraId, string lineId, CancellationToken cancellationToken = default);
    Task<LineDefinition> SaveAsync(string cameraId, LineDefinition line, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LineDefinition>> SaveAllAsync(string cameraId, IReadOnlyList<LineDefinition> lines, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string cameraId, string lineId, CancellationToken cancellationToken = default);
    Task<int> DeleteByCameraAsync(string cameraId, CancellationToken cancellationToken = default);
}

