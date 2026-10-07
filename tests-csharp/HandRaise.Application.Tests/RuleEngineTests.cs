using HandRaise.Application.Rules;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Rules;

namespace HandRaise.Application.Tests;

public sealed class RuleEngineTests
{
    private static AnalyticEvent CreateSampleEvent(
        string id = "ev-1",
        string cameraId = "cam-01",
        string instanceId = "inst-01",
        string eventType = "person_count_updated",
        DateTimeOffset? timestamp = null,
        int? trackId = 1,
        string? zoneId = "zone-a",
        double? confidence = 0.95,
        Dictionary<string, object?>? metadata = null)
    {
        return new AnalyticEvent(
            Id: id,
            CameraId: cameraId,
            AnalyticInstanceId: instanceId,
            AnalyticType: "person_counting",
            EventType: eventType,
            TimestampUtc: timestamp ?? DateTimeOffset.UtcNow,
            TrackId: trackId,
            ZoneId: zoneId,
            Confidence: confidence,
            Metadata: metadata ?? new Dictionary<string, object?>
            {
                ["current_occupancy"] = 55,
                ["direction"] = "in",
                ["line_id"] = "line-1"
            });
    }

    [Fact]
    public void EvaluateCondition_Equals_MatchesCorrectly()
    {
        var ev = CreateSampleEvent();
        var cond1 = new RuleCondition("event_type", RuleOperator.Equals, "person_count_updated");
        var cond2 = new RuleCondition("zone_id", RuleOperator.Equals, "zone-a");
        var cond3 = new RuleCondition("metadata.direction", RuleOperator.Equals, "in");
        var cond4 = new RuleCondition("current_occupancy", RuleOperator.Equals, 55);
        var cond5 = new RuleCondition("current_occupancy", RuleOperator.Equals, 99);

        Assert.True(RuleEngine.EvaluateCondition(cond1, ev));
        Assert.True(RuleEngine.EvaluateCondition(cond2, ev));
        Assert.True(RuleEngine.EvaluateCondition(cond3, ev));
        Assert.True(RuleEngine.EvaluateCondition(cond4, ev));
        Assert.False(RuleEngine.EvaluateCondition(cond5, ev));
    }

    [Fact]
    public void EvaluateCondition_NumericComparisons_WorkAccurately()
    {
        var ev = CreateSampleEvent(); // current_occupancy = 55, confidence = 0.95

        var gt = new RuleCondition("current_occupancy", RuleOperator.GreaterThan, 50);
        var gte = new RuleCondition("current_occupancy", RuleOperator.GreaterOrEqual, 55);
        var lt = new RuleCondition("current_occupancy", RuleOperator.LessThan, 100);
        var lte = new RuleCondition("current_occupancy", RuleOperator.LessOrEqual, 55);
        var failGt = new RuleCondition("current_occupancy", RuleOperator.GreaterThan, 60);

        Assert.True(RuleEngine.EvaluateCondition(gt, ev));
        Assert.True(RuleEngine.EvaluateCondition(gte, ev));
        Assert.True(RuleEngine.EvaluateCondition(lt, ev));
        Assert.True(RuleEngine.EvaluateCondition(lte, ev));
        Assert.False(RuleEngine.EvaluateCondition(failGt, ev));
    }

    [Fact]
    public void EvaluateCondition_Contains_MatchesSubstringAndCollection()
    {
        var ev = CreateSampleEvent();
        var condSubstring = new RuleCondition("event_type", RuleOperator.Contains, "count");
        var condFail = new RuleCondition("event_type", RuleOperator.Contains, "intrusion");

        Assert.True(RuleEngine.EvaluateCondition(condSubstring, ev));
        Assert.False(RuleEngine.EvaluateCondition(condFail, ev));
    }

    [Fact]
    public void Evaluate_MultipleAndConditions_AllMustPass()
    {
        var ev = CreateSampleEvent();
        var rule = new AlertRule(
            Id: "rule-occupancy",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            Name: "Aforo y Dirección",
            Enabled: true,
            Severity: AlertSeverity.High,
            EventTypes: ["person_count_updated"],
            Conditions:
            [
                new RuleCondition("current_occupancy", RuleOperator.GreaterOrEqual, 50),
                new RuleCondition("metadata.direction", RuleOperator.Equals, "in")
            ]);

        var engine = new RuleEngine();
        var alerts = engine.Evaluate(ev, [rule]);

        Assert.Single(alerts);
        Assert.Equal("rule-occupancy", alerts[0].RuleId);
        Assert.Equal("cam-01", alerts[0].CameraId);
        Assert.Equal(AlertSeverity.High, alerts[0].Severity);
    }

    [Fact]
    public void Evaluate_Cooldown_SuppressesRapidAlerts()
    {
        var t0 = DateTimeOffset.UtcNow;
        var rule = new AlertRule(
            Id: "rule-cooldown",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            Name: "Regla con Cooldown",
            Enabled: true,
            Severity: AlertSeverity.Medium,
            EventTypes: ["person_count_updated"],
            CooldownMs: 5000);

        var engine = new RuleEngine();

        // 1st event at t0: should generate alert
        var ev1 = CreateSampleEvent(id: "ev-1", timestamp: t0);
        var alerts1 = engine.Evaluate(ev1, [rule]);
        Assert.Single(alerts1);

        // 2nd event at t0 + 2s: should be suppressed by cooldown
        var ev2 = CreateSampleEvent(id: "ev-2", timestamp: t0.AddSeconds(2));
        var alerts2 = engine.Evaluate(ev2, [rule]);
        Assert.Empty(alerts2);

        // 3rd event at t0 + 6s: cooldown expired, should generate alert
        var ev3 = CreateSampleEvent(id: "ev-3", timestamp: t0.AddSeconds(6));
        var alerts3 = engine.Evaluate(ev3, [rule]);
        Assert.Single(alerts3);
    }

    [Fact]
    public void Evaluate_Idempotency_SameEventAndRuleEvaluatedTwice_OnlyGeneratesOneAlert()
    {
        var ev = CreateSampleEvent(id: "ev-idemp-1");
        var rule = new AlertRule(
            Id: "rule-idemp",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            Name: "Idempotency Rule",
            Enabled: true,
            Severity: AlertSeverity.Low,
            EventTypes: ["person_count_updated"]);

        var engine = new RuleEngine();
        var alerts1 = engine.Evaluate(ev, [rule]);
        Assert.Single(alerts1);

        // Second evaluation with identical event
        var alerts2 = engine.Evaluate(ev, [rule]);
        Assert.Empty(alerts2);
    }

    [Fact]
    public void InterpolateTemplate_ReplacesStandardAndMetadataVariables()
    {
        var ev = CreateSampleEvent(
            id: "ev-template-1",
            cameraId: "cam-lobby",
            confidence: 0.98,
            metadata: new Dictionary<string, object?> { ["current_occupancy"] = 72 });

        var rule = new AlertRule(
            Id: "rule-tmpl",
            CameraId: "cam-lobby",
            AnalyticInstanceId: "inst-01",
            Name: "Exceso de Aforo",
            Enabled: true,
            Severity: AlertSeverity.Critical,
            EventTypes: ["person_count_updated"],
            TitleTemplate: "[{severity}] Alerta en {camera_id}: {rule_name}",
            DescriptionTemplate: "Evento {event_type} con ocupación {metadata.current_occupancy} (conf: {confidence})");

        var title = RuleEngine.InterpolateTemplate(rule.TitleTemplate, ev, rule);
        var desc = RuleEngine.InterpolateTemplate(rule.DescriptionTemplate, ev, rule);

        Assert.Equal("[critical] Alerta en cam-lobby: Exceso de Aforo", title);
        Assert.Equal("Evento person_count_updated con ocupación 72 (conf: 0.98)", desc);
    }

    [Fact]
    public async Task ProcessEventAsync_SavesAlertToStore()
    {
        var rule = new AlertRule(
            Id: "rule-process",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            Name: "Process Rule",
            Enabled: true,
            Severity: AlertSeverity.High,
            EventTypes: ["person_count_updated"]);

        var ruleProvider = new TestRuleProvider([rule]);
        var alertStore = new InMemoryTestAlertStore();
        var engine = new RuleEngine(ruleProvider, alertStore);

        var ev = CreateSampleEvent(id: "ev-proc-1");
        var savedAlerts = await engine.ProcessEventAsync(ev);

        Assert.Single(savedAlerts);
        var stored = await alertStore.GetByIdAsync(savedAlerts[0].Id);
        Assert.NotNull(stored);
        Assert.Equal(AlertStatus.Open, stored!.Status);
        Assert.Equal("rule-process", stored.RuleId);
    }

    [Fact]
    public async Task ProcessEventAsync_HandRaiseEvent_MatchesRuleAndEmitsAlert()
    {
        var rule = new AlertRule(
            Id: "rule-hand-1",
            CameraId: "cam-01",
            AnalyticInstanceId: "an-cam1-handraise",
            Name: "Alerta Mano Levantada",
            Enabled: true,
            Severity: AlertSeverity.High,
            EventTypes: ["hand_raised"]);

        var ruleProvider = new TestRuleProvider([rule]);
        var alertStore = new InMemoryTestAlertStore();
        var engine = new RuleEngine(ruleProvider, alertStore);

        // Simulate event created with instance ID
        var ev = new AnalyticEvent(
            Id: "ev-hand-101",
            CameraId: "cam-01",
            AnalyticInstanceId: "an-cam1-handraise",
            AnalyticType: "hand_raise",
            EventType: "hand_raised",
            TimestampUtc: DateTimeOffset.UtcNow,
            Confidence: 0.95);

        var alerts = await engine.ProcessEventAsync(ev);
        Assert.Single(alerts);
        Assert.Equal("rule-hand-1", alerts[0].RuleId);
        Assert.Equal("cam-01", alerts[0].CameraId);
        Assert.Equal(AlertSeverity.High, alerts[0].Severity);
    }

    [Fact]
    public async Task ProcessEventAsync_LegacyHandRaiseInstance_MatchesRuleViaFallback()
    {
        var rule = new AlertRule(
            Id: "rule-hand-2",
            CameraId: "cam-01",
            AnalyticInstanceId: "an-cam1-handraise",
            Name: "Alerta Mano Levantada",
            Enabled: true,
            Severity: AlertSeverity.Critical,
            EventTypes: ["hand_raised"]);

        var ruleProvider = new TestRuleProvider([rule]);
        var alertStore = new InMemoryTestAlertStore();
        var engine = new RuleEngine(ruleProvider, alertStore);

        // Simulate event with legacy_hand_raise instance ID
        var ev = new AnalyticEvent(
            Id: "ev-hand-102",
            CameraId: "cam-01",
            AnalyticInstanceId: "legacy_hand_raise",
            AnalyticType: "hand_raise",
            EventType: "hand_raised",
            TimestampUtc: DateTimeOffset.UtcNow,
            Confidence: 0.92);

        var alerts = await engine.ProcessEventAsync(ev);
        Assert.Single(alerts);
        Assert.Equal("rule-hand-2", alerts[0].RuleId);
        Assert.Equal(AlertSeverity.Critical, alerts[0].Severity);
    }

    [Fact]
    public async Task ProcessEventAsync_DisabledRule_EmitsZeroAlerts()
    {
        var rule = new AlertRule(
            Id: "rule-disabled",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            Name: "Disabled Rule",
            Enabled: false,
            Severity: AlertSeverity.High,
            EventTypes: ["hand_raised"]);

        var ruleProvider = new TestRuleProvider([rule]);
        var alertStore = new InMemoryTestAlertStore();
        var engine = new RuleEngine(ruleProvider, alertStore);

        var ev = new AnalyticEvent(
            Id: "ev-hand-103",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            AnalyticType: "hand_raise",
            EventType: "hand_raised",
            TimestampUtc: DateTimeOffset.UtcNow);

        var alerts = await engine.ProcessEventAsync(ev);
        Assert.Empty(alerts);
    }

    [Fact]
    public async Task ProcessEventAsync_Cooldown_SuppressesDuplicateAlertsWithinWindow()
    {
        var rule = new AlertRule(
            Id: "rule-cooldown",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            Name: "Cooldown Rule",
            Enabled: true,
            Severity: AlertSeverity.Medium,
            EventTypes: ["hand_raised"],
            CooldownMs: 5000);

        var ruleProvider = new TestRuleProvider([rule]);
        var alertStore = new InMemoryTestAlertStore();
        var engine = new RuleEngine(ruleProvider, alertStore);

        var baseTime = DateTimeOffset.UtcNow;
        var ev1 = new AnalyticEvent("ev-1", "cam-01", "inst-01", "hand_raise", "hand_raised", baseTime);
        var ev2 = new AnalyticEvent("ev-2", "cam-01", "inst-01", "hand_raise", "hand_raised", baseTime.AddSeconds(2));
        var ev3 = new AnalyticEvent("ev-3", "cam-01", "inst-01", "hand_raise", "hand_raised", baseTime.AddSeconds(6));

        var alerts1 = await engine.ProcessEventAsync(ev1);
        var alerts2 = await engine.ProcessEventAsync(ev2);
        var alerts3 = await engine.ProcessEventAsync(ev3);

        Assert.Single(alerts1);
        Assert.Empty(alerts2); // Suppressed within 5s cooldown
        Assert.Single(alerts3); // Emitted after 5s cooldown
    }

    private sealed class TestRuleProvider(IReadOnlyList<AlertRule> rules) : IRuleProvider
    {
        public IReadOnlyList<AlertRule> GetRules(string cameraId) =>
            rules.Where(r => string.Equals(r.CameraId, cameraId, StringComparison.OrdinalIgnoreCase)).ToArray();

        public IReadOnlyList<AlertRule> GetAllRules() => rules;
    }

    private sealed class InMemoryTestAlertStore : IAlertStore
    {
        private readonly List<OperationalAlert> _alerts = [];

        public Task<IReadOnlyList<OperationalAlert>> QueryAsync(AlertQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OperationalAlert>>(_alerts.ToArray());

        public Task<OperationalAlert?> GetByIdAsync(string alertId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_alerts.FirstOrDefault(a => a.Id == alertId));

        public Task<OperationalAlert> SaveAsync(OperationalAlert alert, CancellationToken cancellationToken = default)
        {
            _alerts.RemoveAll(a => a.Id == alert.Id);
            _alerts.Add(alert);
            return Task.FromResult(alert);
        }

        public Task<OperationalAlert?> AcknowledgeAsync(string alertId, DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
        {
            var idx = _alerts.FindIndex(a => a.Id == alertId);
            if (idx >= 0)
            {
                var updated = _alerts[idx] with { Status = AlertStatus.Acknowledged, AcknowledgedAt = timestampUtc };
                _alerts[idx] = updated;
                return Task.FromResult<OperationalAlert?>(updated);
            }
            return Task.FromResult<OperationalAlert?>(null);
        }

        public Task<OperationalAlert?> ResolveAsync(string alertId, DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
        {
            var idx = _alerts.FindIndex(a => a.Id == alertId);
            if (idx >= 0)
            {
                var updated = _alerts[idx] with { Status = AlertStatus.Resolved, ResolvedAt = timestampUtc };
                _alerts[idx] = updated;
                return Task.FromResult<OperationalAlert?>(updated);
            }
            return Task.FromResult<OperationalAlert?>(null);
        }

        public Task<bool> ExistsForEventAndRuleAsync(string sourceEventId, string ruleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_alerts.Any(a => a.SourceEventId == sourceEventId && a.RuleId == ruleId));
    }
}
