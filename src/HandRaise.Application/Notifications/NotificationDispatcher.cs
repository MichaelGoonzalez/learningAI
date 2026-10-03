using System.Text.RegularExpressions;
using HandRaise.Domain.Notifications;
using HandRaise.Domain.Rules;

namespace HandRaise.Application.Notifications;

public sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly INotificationPolicyStore _policyStore;
    private readonly INotificationDestinationStore _destinationStore;
    private readonly INotificationAttemptStore _attemptStore;
    private readonly Dictionary<NotificationDestinationType, INotificationSender> _senders;
    private readonly string _nodeId;
    private readonly string _siteId;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly Action<string>? _log;

    public NotificationDispatcher(
        INotificationPolicyStore policyStore,
        INotificationDestinationStore destinationStore,
        INotificationAttemptStore attemptStore,
        IEnumerable<INotificationSender> senders,
        string nodeId = "local-node",
        string siteId = "default-site",
        IReadOnlyList<TimeSpan>? retryDelays = null,
        Action<string>? log = null)
    {
        _policyStore = policyStore ?? throw new ArgumentNullException(nameof(policyStore));
        _destinationStore = destinationStore ?? throw new ArgumentNullException(nameof(destinationStore));
        _attemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        _senders = senders?.ToDictionary(s => s.SupportedType) ?? [];
        _nodeId = nodeId;
        _siteId = siteId;
        _retryDelays = retryDelays ?? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
        _log = log;
    }

    public async Task DispatchAsync(
        OperationalAlert alert,
        string? analyticTypeId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        IReadOnlyList<NotificationPolicy> policies;
        try
        {
            policies = await _policyStore.ListAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Error al cargar políticas de notificación: {Sanitize(ex.Message)}");
            return;
        }

        var matchingPolicies = policies.Where(p => p.Matches(alert, analyticTypeId)).ToList();
        if (matchingPolicies.Count == 0) return;

        foreach (var policy in matchingPolicies)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var destination = await _destinationStore.GetByIdAsync(policy.DestinationId, cancellationToken);
                if (destination == null || !destination.Enabled)
                {
                    continue;
                }

                if (!_senders.TryGetValue(destination.Type, out var sender))
                {
                    var failedAttempt = new NotificationAttempt(
                        Id: $"att-{Guid.NewGuid():N}"[..12],
                        AlertId: alert.Id,
                        PolicyId: policy.Id,
                        DestinationId: destination.Id,
                        AttemptNumber: 1,
                        Status: NotificationStatus.Failed,
                        TimestampUtc: DateTimeOffset.UtcNow,
                        ResponseCode: null,
                        ErrorSanitized: $"No hay emisor registrado para el tipo de destino '{destination.Type}'.");
                    await _attemptStore.SaveAsync(failedAttempt, cancellationToken);
                    continue;
                }

                var payload = new WebhookPayload(
                    SchemaVersion: "1.0",
                    NodeId: _nodeId,
                    SiteId: _siteId,
                    Alert: alert,
                    DispatchedAt: DateTimeOffset.UtcNow,
                    IsTest: false);

                var maxAttempts = 1 + _retryDelays.Count;
                for (var attemptNum = 1; attemptNum <= maxAttempts; attemptNum++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    NotificationSenderResult sendResult;
                    try
                    {
                        sendResult = await sender.SendAsync(destination, payload, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        sendResult = new NotificationSenderResult(
                            Success: false,
                            StatusCode: null,
                            Error: ex.Message);
                    }

                    var status = sendResult.Success ? NotificationStatus.Succeeded : NotificationStatus.Failed;
                    var attempt = new NotificationAttempt(
                        Id: $"att-{Guid.NewGuid():N}"[..12],
                        AlertId: alert.Id,
                        PolicyId: policy.Id,
                        DestinationId: destination.Id,
                        AttemptNumber: attemptNum,
                        Status: status,
                        TimestampUtc: DateTimeOffset.UtcNow,
                        ResponseCode: sendResult.StatusCode,
                        ErrorSanitized: Sanitize(sendResult.Error),
                        Metadata: sendResult.Metadata);

                    try
                    {
                        await _attemptStore.SaveAsync(attempt, cancellationToken);
                    }
                    catch (Exception saveEx)
                    {
                        _log?.Invoke($"Error al guardar intento de notificación: {Sanitize(saveEx.Message)}");
                    }

                    if (sendResult.Success)
                    {
                        break;
                    }

                    if (attemptNum < maxAttempts)
                    {
                        var delay = _retryDelays[attemptNum - 1];
                        try
                        {
                            await Task.Delay(delay, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Fallo aislado despachando política '{policy.Id}' a destino '{policy.DestinationId}': {Sanitize(ex.Message)}");
            }
        }
    }

    public async Task<NotificationAttempt> TestDestinationAsync(
        NotificationDestination destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Validate();

        if (!_senders.TryGetValue(destination.Type, out var sender))
        {
            var failedAttempt = new NotificationAttempt(
                Id: $"test-{Guid.NewGuid():N}"[..12],
                AlertId: "test-alert",
                PolicyId: "test-policy",
                DestinationId: destination.Id,
                AttemptNumber: 1,
                Status: NotificationStatus.Failed,
                TimestampUtc: DateTimeOffset.UtcNow,
                ResponseCode: null,
                ErrorSanitized: $"No hay emisor registrado para el tipo de destino '{destination.Type}'.");
            return failedAttempt;
        }

        var dummyAlert = new OperationalAlert(
            Id: "test-alert-001",
            RuleId: "test-rule",
            CameraId: "test-camera",
            AnalyticInstanceId: "test-instance",
            SourceEventId: "test-event-001",
            Severity: AlertSeverity.Low,
            Status: AlertStatus.Open,
            Title: "Test Notification Alert",
            Description: "Esta es una notificación de prueba operativa desde VisionControl Edge.",
            CreatedAt: DateTimeOffset.UtcNow);

        var payload = new WebhookPayload(
            SchemaVersion: "1.0",
            NodeId: _nodeId,
            SiteId: _siteId,
            Alert: dummyAlert,
            DispatchedAt: DateTimeOffset.UtcNow,
            IsTest: true);

        NotificationSenderResult sendResult;
        try
        {
            sendResult = await sender.SendAsync(destination, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            sendResult = new NotificationSenderResult(
                Success: false,
                StatusCode: null,
                Error: ex.Message);
        }

        var attempt = new NotificationAttempt(
            Id: $"test-{Guid.NewGuid():N}"[..12],
            AlertId: dummyAlert.Id,
            PolicyId: "test-policy",
            DestinationId: destination.Id,
            AttemptNumber: 1,
            Status: sendResult.Success ? NotificationStatus.Succeeded : NotificationStatus.Failed,
            TimestampUtc: DateTimeOffset.UtcNow,
            ResponseCode: sendResult.StatusCode,
            ErrorSanitized: Sanitize(sendResult.Error),
            Metadata: sendResult.Metadata);

        try
        {
            await _attemptStore.SaveAsync(attempt, cancellationToken);
        }
        catch
        {
            // Ignore persistence error during test
        }

        return attempt;
    }

    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // Redact Bearer / Basic / token / password strings
        var sanitized = Regex.Replace(text, @"(Bearer\s+)[A-Za-z0-9\-\._~\+\/]+=*", "$1[REDACTED]", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(Basic\s+)[A-Za-z0-9\-\._~\+\/]+=*", "$1[REDACTED]", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(password|secret|token|api_key|apikey)=([^\s&]+)", "$1=[REDACTED]", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(""password""\s*:\s*"")[^""]+("")", "$1[REDACTED]$2", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(""secret""\s*:\s*"")[^""]+("")", "$1[REDACTED]$2", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(""token""\s*:\s*"")[^""]+("")", "$1[REDACTED]$2", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(""apiKey""\s*:\s*"")[^""]+("")", "$1[REDACTED]$2", RegexOptions.IgnoreCase);

        return sanitized;
    }
}
