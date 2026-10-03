using HandRaise.Domain.Analytics;

namespace HandRaise.Application.Analytics;

public interface IAnalyticCatalog
{
    IReadOnlyList<AnalyticDefinition> GetAll();
    AnalyticDefinition? GetById(string id);
    (bool IsValid, string? ErrorMessage) ValidateConfiguration(string analyticTypeId, IReadOnlyDictionary<string, object?>? configuration);
}
