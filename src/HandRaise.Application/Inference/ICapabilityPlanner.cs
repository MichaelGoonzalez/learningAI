using HandRaise.Application.Analytics;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;

namespace HandRaise.Application.Inference;

public interface ICapabilityPlanner
{
    CapabilityExecutionPlan CreatePlan(
        string cameraId,
        IReadOnlyList<CameraAnalyticInstance> activeAnalytics,
        IAnalyticCatalog catalog);
}

