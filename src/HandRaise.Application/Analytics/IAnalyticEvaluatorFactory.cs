using HandRaise.Domain.Analytics;
using HandRaise.Domain.Detection;

namespace HandRaise.Application.Analytics;

public interface IAnalyticEvaluatorFactory
{
    IAnalyticEvaluator CreateEvaluator(CameraAnalyticInstance instance, DetectionOptions defaultOptions);
}
