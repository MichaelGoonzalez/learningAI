namespace HandRaise.Application.Analytics;

public interface IAnalyticEvaluatorProvider
{
    IReadOnlyList<IAnalyticEvaluator> GetEvaluators();
}

public sealed class AtomicEvaluatorProvider : IAnalyticEvaluatorProvider
{
    private IReadOnlyList<IAnalyticEvaluator> _evaluators;

    public AtomicEvaluatorProvider(IReadOnlyList<IAnalyticEvaluator>? initial = null)
    {
        _evaluators = initial?.ToArray() ?? [];
    }

    public IReadOnlyList<IAnalyticEvaluator> GetEvaluators() => Volatile.Read(ref _evaluators);

    public void Replace(IReadOnlyList<IAnalyticEvaluator> evaluators)
    {
        ArgumentNullException.ThrowIfNull(evaluators);
        Volatile.Write(ref _evaluators, evaluators.ToArray());
    }
}
