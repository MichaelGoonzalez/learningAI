using HandRaise.Application.Events;
using HandRaise.Application.Notifications;
using HandRaise.Application.Rules;

namespace HandRaise.Host.Services;

public sealed class AlertEngineWorker(
    HandEventBus bus,
    IRuleEngine ruleEngine,
    ILogger<AlertEngineWorker> logger,
    INotificationDispatcher? notificationDispatcher = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var subscription = bus.Subscribe();
        try
        {
            await foreach (var handEvent in subscription.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var analyticEvent = AnalyticEventAdapter.FromLegacyHandEvent(handEvent);
                    var alerts = await ruleEngine.ProcessEventAsync(analyticEvent, stoppingToken);
                    if (alerts.Count > 0 && notificationDispatcher != null)
                    {
                        foreach (var alert in alerts)
                        {
                            try
                            {
                                await notificationDispatcher.DispatchAsync(alert, analyticEvent.AnalyticType, stoppingToken);
                            }
                            catch (Exception dispEx)
                            {
                                logger.LogWarning(dispEx, "Error dispatching notifications for alert {AlertId}: {Message}", alert.Id, dispEx.Message);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error processing event {EventId} through RuleEngine: {Message}", handEvent.Id, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
