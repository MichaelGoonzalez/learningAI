using HandRaise.Domain.Rules;

namespace HandRaise.Application.Rules;

public sealed record AlertQuery(
    string? CameraId = null,
    string? AnalyticInstanceId = null,
    AlertSeverity? Severity = null,
    AlertStatus? Status = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 100,
    int Offset = 0);

public interface IAlertStore
{
    Task<IReadOnlyList<OperationalAlert>> QueryAsync(AlertQuery query, CancellationToken cancellationToken = default);
    Task<OperationalAlert?> GetByIdAsync(string alertId, CancellationToken cancellationToken = default);
    Task<OperationalAlert> SaveAsync(OperationalAlert alert, CancellationToken cancellationToken = default);
    Task<OperationalAlert?> AcknowledgeAsync(string alertId, DateTimeOffset timestampUtc, CancellationToken cancellationToken = default);
    Task<OperationalAlert?> ResolveAsync(string alertId, DateTimeOffset timestampUtc, CancellationToken cancellationToken = default);
    Task<bool> ExistsForEventAndRuleAsync(string sourceEventId, string ruleId, CancellationToken cancellationToken = default);
}
