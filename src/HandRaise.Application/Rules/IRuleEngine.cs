using HandRaise.Domain.Analytics;
using HandRaise.Domain.Rules;

namespace HandRaise.Application.Rules;

public interface IRuleEngine
{
    Task<IReadOnlyList<OperationalAlert>> ProcessEventAsync(AnalyticEvent ev, CancellationToken cancellationToken = default);
    IReadOnlyList<OperationalAlert> Evaluate(AnalyticEvent ev, IReadOnlyList<AlertRule> rules);
    void Reset();
}
