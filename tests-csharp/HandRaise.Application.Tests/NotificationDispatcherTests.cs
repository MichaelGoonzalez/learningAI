using HandRaise.Application.Notifications;
using HandRaise.Domain.Notifications;
using HandRaise.Domain.Rules;

namespace HandRaise.Application.Tests;

public sealed class NotificationDispatcherTests
{
    private static OperationalAlert CreateSampleAlert(string id = "alt-01", AlertSeverity severity = AlertSeverity.High) =>
        new(
            Id: id,
            RuleId: "rule-1",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            SourceEventId: "ev-01",
            Severity: severity,
            Status: AlertStatus.Open,
            Title: "Alerta de Prueba",
            Description: "Descripción de prueba",
            CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task DispatchAsync_SuccessfulSend_RecordsSucceededAttempt()
    {
        var dest = new NotificationDestination(
            Id: "dest-1",
            Name: "Webhook 1",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?> { ["url"] = "https://example.com/webhook" });

        var policy = new NotificationPolicy(
            Id: "pol-1",
            Name: "Policy 1",
            Enabled: true,
            DestinationId: "dest-1");

        var policyStore = new FakePolicyStore([policy]);
        var destinationStore = new FakeDestinationStore([dest]);
        var attemptStore = new FakeAttemptStore();

        var mockSender = new MockNotificationSender(NotificationDestinationType.Webhook, (d, p) =>
            Task.FromResult(new NotificationSenderResult(true, 200, null)));

        var dispatcher = new NotificationDispatcher(
            policyStore,
            destinationStore,
            attemptStore,
            [mockSender],
            retryDelays: [TimeSpan.FromMilliseconds(10)]);

        var alert = CreateSampleAlert();
        await dispatcher.DispatchAsync(alert);

        var attempts = await attemptStore.QueryAsync(new NotificationAttemptQuery());
        Assert.Single(attempts);
        Assert.Equal(NotificationStatus.Succeeded, attempts[0].Status);
        Assert.Equal(1, attempts[0].AttemptNumber);
        Assert.Equal("dest-1", attempts[0].DestinationId);
        Assert.Equal("pol-1", attempts[0].PolicyId);
    }

    [Fact]
    public async Task DispatchAsync_DisabledDestination_SuppressesDispatch()
    {
        var dest = new NotificationDestination(
            Id: "dest-disabled",
            Name: "Disabled Webhook",
            Type: NotificationDestinationType.Webhook,
            Enabled: false,
            Configuration: new Dictionary<string, object?> { ["url"] = "https://example.com/webhook" });

        var policy = new NotificationPolicy(
            Id: "pol-1",
            Name: "Policy 1",
            Enabled: true,
            DestinationId: "dest-disabled");

        var policyStore = new FakePolicyStore([policy]);
        var destinationStore = new FakeDestinationStore([dest]);
        var attemptStore = new FakeAttemptStore();

        var sendCount = 0;
        var mockSender = new MockNotificationSender(NotificationDestinationType.Webhook, (d, p) =>
        {
            sendCount++;
            return Task.FromResult(new NotificationSenderResult(true, 200));
        });

        var dispatcher = new NotificationDispatcher(
            policyStore,
            destinationStore,
            attemptStore,
            [mockSender]);

        var alert = CreateSampleAlert();
        await dispatcher.DispatchAsync(alert);

        Assert.Equal(0, sendCount);
        var attempts = await attemptStore.QueryAsync(new NotificationAttemptQuery());
        Assert.Empty(attempts);
    }

    [Fact]
    public async Task DispatchAsync_RetriesOnTransientFailure_AndSucceeds()
    {
        var dest = new NotificationDestination(
            Id: "dest-retry",
            Name: "Retry Webhook",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?> { ["url"] = "https://example.com/webhook" });

        var policy = new NotificationPolicy(
            Id: "pol-1",
            Name: "Policy 1",
            Enabled: true,
            DestinationId: "dest-retry");

        var policyStore = new FakePolicyStore([policy]);
        var destinationStore = new FakeDestinationStore([dest]);
        var attemptStore = new FakeAttemptStore();

        var callIndex = 0;
        var mockSender = new MockNotificationSender(NotificationDestinationType.Webhook, (d, p) =>
        {
            callIndex++;
            if (callIndex == 1)
            {
                return Task.FromResult(new NotificationSenderResult(false, 503, "Service Unavailable"));
            }
            return Task.FromResult(new NotificationSenderResult(true, 200));
        });

        var dispatcher = new NotificationDispatcher(
            policyStore,
            destinationStore,
            attemptStore,
            [mockSender],
            retryDelays: [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10)]);

        var alert = CreateSampleAlert();
        await dispatcher.DispatchAsync(alert);

        Assert.Equal(2, callIndex);
        var attempts = await attemptStore.QueryAsync(new NotificationAttemptQuery());
        Assert.Equal(2, attempts.Count);
        Assert.Equal(NotificationStatus.Failed, attempts.First(a => a.AttemptNumber == 1).Status);
        Assert.Equal(NotificationStatus.Succeeded, attempts.First(a => a.AttemptNumber == 2).Status);
    }

    [Fact]
    public async Task DispatchAsync_FailureIsolation_OneFailingPolicyDoesNotBlockOtherPolicies()
    {
        var destFail = new NotificationDestination(
            Id: "dest-fail",
            Name: "Failing Webhook",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?> { ["url"] = "https://fail.com" });

        var destOk = new NotificationDestination(
            Id: "dest-ok",
            Name: "Ok MQTT",
            Type: NotificationDestinationType.Mqtt,
            Enabled: true,
            Configuration: new Dictionary<string, object?> { ["broker"] = "localhost" });

        var pol1 = new NotificationPolicy("pol-1", "Pol 1", true, "dest-fail");
        var pol2 = new NotificationPolicy("pol-2", "Pol 2", true, "dest-ok");

        var policyStore = new FakePolicyStore([pol1, pol2]);
        var destinationStore = new FakeDestinationStore([destFail, destOk]);
        var attemptStore = new FakeAttemptStore();

        var webhookSender = new MockNotificationSender(NotificationDestinationType.Webhook, (d, p) =>
            Task.FromResult(new NotificationSenderResult(false, 500, "Webhook Error")));

        var mqttSender = new MockNotificationSender(NotificationDestinationType.Mqtt, (d, p) =>
            Task.FromResult(new NotificationSenderResult(true, 0)));

        var dispatcher = new NotificationDispatcher(
            policyStore,
            destinationStore,
            attemptStore,
            [webhookSender, mqttSender],
            retryDelays: []);

        var alert = CreateSampleAlert();
        await dispatcher.DispatchAsync(alert);

        var attempts = await attemptStore.QueryAsync(new NotificationAttemptQuery());
        Assert.Equal(2, attempts.Count);
        Assert.Contains(attempts, a => a.DestinationId == "dest-fail" && a.Status == NotificationStatus.Failed);
        Assert.Contains(attempts, a => a.DestinationId == "dest-ok" && a.Status == NotificationStatus.Succeeded);
    }

    [Fact]
    public async Task TestDestinationAsync_SendsTestPayload()
    {
        var dest = new NotificationDestination(
            Id: "dest-test",
            Name: "Test Webhook",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?> { ["url"] = "https://test.com" });

        var policyStore = new FakePolicyStore([]);
        var destinationStore = new FakeDestinationStore([dest]);
        var attemptStore = new FakeAttemptStore();

        WebhookPayload? receivedPayload = null;
        var mockSender = new MockNotificationSender(NotificationDestinationType.Webhook, (d, p) =>
        {
            receivedPayload = p;
            return Task.FromResult(new NotificationSenderResult(true, 200));
        });

        var dispatcher = new NotificationDispatcher(
            policyStore,
            destinationStore,
            attemptStore,
            [mockSender]);

        var result = await dispatcher.TestDestinationAsync(dest);
        Assert.NotNull(result);
        Assert.Equal(NotificationStatus.Succeeded, result.Status);
        Assert.NotNull(receivedPayload);
        Assert.True(receivedPayload!.IsTest);
    }

    [Fact]
    public void Sanitize_RedactsTokensAndPasswords()
    {
        var raw1 = "Error 401 with Bearer secret-token-123456789";
        var raw2 = "Failed to connect with password=supersecretpass and token=my-token-123";

        var sanitized1 = NotificationDispatcher.Sanitize(raw1);
        var sanitized2 = NotificationDispatcher.Sanitize(raw2);

        Assert.DoesNotContain("secret-token-123456789", sanitized1);
        Assert.Contains("Bearer [REDACTED]", sanitized1);

        Assert.DoesNotContain("supersecretpass", sanitized2);
        Assert.DoesNotContain("my-token-123", sanitized2);
        Assert.Contains("password=[REDACTED]", sanitized2);
        Assert.Contains("token=[REDACTED]", sanitized2);
    }

    private sealed class FakePolicyStore(IReadOnlyList<NotificationPolicy> policies) : INotificationPolicyStore
    {
        private readonly List<NotificationPolicy> _list = [.. policies];
        public Task<IReadOnlyList<NotificationPolicy>> ListAllAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<NotificationPolicy>>(_list);
        public Task<NotificationPolicy?> GetByIdAsync(string id, CancellationToken token = default) => Task.FromResult(_list.FirstOrDefault(p => p.Id == id));
        public Task<NotificationPolicy> SaveAsync(NotificationPolicy policy, CancellationToken token = default) { _list.RemoveAll(p => p.Id == policy.Id); _list.Add(policy); return Task.FromResult(policy); }
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => Task.FromResult(_list.RemoveAll(p => p.Id == id) > 0);
    }

    private sealed class FakeDestinationStore(IReadOnlyList<NotificationDestination> destinations) : INotificationDestinationStore
    {
        private readonly List<NotificationDestination> _list = [.. destinations];
        public Task<IReadOnlyList<NotificationDestination>> ListAllAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<NotificationDestination>>(_list);
        public Task<NotificationDestination?> GetByIdAsync(string id, CancellationToken token = default) => Task.FromResult(_list.FirstOrDefault(d => d.Id == id));
        public Task<NotificationDestination> SaveAsync(NotificationDestination dest, CancellationToken token = default) { _list.RemoveAll(d => d.Id == dest.Id); _list.Add(dest); return Task.FromResult(dest); }
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => Task.FromResult(_list.RemoveAll(d => d.Id == id) > 0);
    }

    private sealed class FakeAttemptStore : INotificationAttemptStore
    {
        private readonly List<NotificationAttempt> _list = [];
        public Task<NotificationAttempt> SaveAsync(NotificationAttempt attempt, CancellationToken token = default) { _list.Add(attempt); return Task.FromResult(attempt); }
        public Task<IReadOnlyList<NotificationAttempt>> QueryAsync(NotificationAttemptQuery query, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<NotificationAttempt>>(_list);
    }

    private sealed class MockNotificationSender(
        NotificationDestinationType supportedType,
        Func<NotificationDestination, WebhookPayload, Task<NotificationSenderResult>> handler) : INotificationSender
    {
        public NotificationDestinationType SupportedType => supportedType;
        public Task<NotificationSenderResult> SendAsync(NotificationDestination destination, WebhookPayload payload, CancellationToken cancellationToken = default) =>
            handler(destination, payload);
    }
}
