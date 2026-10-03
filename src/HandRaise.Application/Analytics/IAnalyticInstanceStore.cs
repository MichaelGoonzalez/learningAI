using HandRaise.Domain.Analytics;

namespace HandRaise.Application.Analytics;

public interface IAnalyticInstanceStore
{
    Task<IReadOnlyList<CameraAnalyticInstance>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CameraAnalyticInstance>> GetByCameraIdAsync(string cameraId, CancellationToken cancellationToken = default);
    Task<CameraAnalyticInstance?> GetByIdAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<bool> SaveAsync(CameraAnalyticInstance instance, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string instanceId, CancellationToken cancellationToken = default);
}
