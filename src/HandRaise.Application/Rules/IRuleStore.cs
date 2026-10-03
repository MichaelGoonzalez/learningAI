using HandRaise.Domain.Rules;

namespace HandRaise.Application.Rules;

public interface IRuleProvider
{
    IReadOnlyList<AlertRule> GetRules(string cameraId);
    IReadOnlyList<AlertRule> GetAllRules();
}

public interface IRuleStore : IRuleProvider
{
    Task<IReadOnlyList<AlertRule>> ListByInstanceAsync(string cameraId, string instanceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AlertRule>> ListByCameraAsync(string cameraId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AlertRule>> ListAllAsync(CancellationToken cancellationToken = default);
    Task<AlertRule?> GetByIdAsync(string cameraId, string instanceId, string ruleId, CancellationToken cancellationToken = default);
    Task<AlertRule> SaveAsync(AlertRule rule, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string cameraId, string instanceId, string ruleId, CancellationToken cancellationToken = default);
    Task<int> DeleteByCameraAsync(string cameraId, CancellationToken cancellationToken = default);
}
