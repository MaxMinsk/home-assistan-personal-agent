using System.Threading;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: runtime telemetry для adaptive LLM routing.
/// Зачем: HAAG-048 требует видеть decision/fallback/bucket counters в /status и логах без обращения к внешним метрикам.
/// Как: AgentRuntime пишет counters на каждый run, а AgentStatusTool возвращает snapshot в безопасном status.
/// </summary>
public sealed class LlmRoutingTelemetry
{
    private long _decisionsTotal;
    private long _decisionsOff;
    private long _decisionsShadow;
    private long _decisionsEnforced;
    private long _smallModelTargetDecisions;
    private long _fallbackToDefaultCount;
    private long _bucketSmallDisabled;
    private long _bucketDefaultProviderDefault;
    private long _bucketDefaultDeep;

    private string _lastRouterMode = "off";
    private string _lastModelTarget = LlmRoutingDecision.ModelTargetDefault;
    private string _lastReasoningTarget = LlmRoutingDecision.ReasoningTargetProviderDefault;
    private string _lastDecisionBucket = LlmRoutingDecision.DecisionBucketDefaultProviderDefault;
    private bool _lastApplied;
    private bool _lastFallbackApplied;
    private string _lastDecisionReason = "no decisions yet.";

    public void RecordDecision(LlmRoutingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        Interlocked.Increment(ref _decisionsTotal);
        if (string.Equals(decision.RouterMode, "shadow", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _decisionsShadow);
        }
        else if (string.Equals(decision.RouterMode, "enforced", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _decisionsEnforced);
        }
        else
        {
            Interlocked.Increment(ref _decisionsOff);
        }

        if (decision.UsesSmallModelTarget)
        {
            Interlocked.Increment(ref _smallModelTargetDecisions);
        }

        _lastRouterMode = decision.RouterMode;
        _lastModelTarget = decision.ModelTarget;
        _lastReasoningTarget = decision.ReasoningTarget;
        _lastDecisionBucket = decision.DecisionBucket;
        _lastApplied = decision.IsApplied;
        _lastDecisionReason = decision.Reason;
    }

    public void RecordExecutionBucket(string bucket, bool fallbackApplied)
    {
        if (string.Equals(bucket, LlmRoutingDecision.DecisionBucketSmallDisabled, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _bucketSmallDisabled);
        }
        else if (string.Equals(bucket, LlmRoutingDecision.DecisionBucketDefaultDeep, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _bucketDefaultDeep);
        }
        else
        {
            Interlocked.Increment(ref _bucketDefaultProviderDefault);
        }

        if (fallbackApplied)
        {
            Interlocked.Increment(ref _fallbackToDefaultCount);
        }

        _lastFallbackApplied = fallbackApplied;
    }

    public LlmRoutingTelemetrySnapshot Snapshot() =>
        new(
            DecisionsTotal: Interlocked.Read(ref _decisionsTotal),
            DecisionsOff: Interlocked.Read(ref _decisionsOff),
            DecisionsShadow: Interlocked.Read(ref _decisionsShadow),
            DecisionsEnforced: Interlocked.Read(ref _decisionsEnforced),
            SmallModelTargetDecisions: Interlocked.Read(ref _smallModelTargetDecisions),
            FallbackToDefaultCount: Interlocked.Read(ref _fallbackToDefaultCount),
            BucketSmallDisabled: Interlocked.Read(ref _bucketSmallDisabled),
            BucketDefaultProviderDefault: Interlocked.Read(ref _bucketDefaultProviderDefault),
            BucketDefaultDeep: Interlocked.Read(ref _bucketDefaultDeep),
            LastRouterMode: _lastRouterMode,
            LastModelTarget: _lastModelTarget,
            LastReasoningTarget: _lastReasoningTarget,
            LastDecisionBucket: _lastDecisionBucket,
            LastApplied: _lastApplied,
            LastFallbackApplied: _lastFallbackApplied,
            LastDecisionReason: _lastDecisionReason);
}

/// <summary>
/// Что: immutable snapshot routing telemetry.
/// Зачем: /status и логи должны получать стабильный срез counters без гонок.
/// Как: создается через LlmRoutingTelemetry.Snapshot после atomic чтения счетчиков.
/// </summary>
public sealed record LlmRoutingTelemetrySnapshot(
    long DecisionsTotal,
    long DecisionsOff,
    long DecisionsShadow,
    long DecisionsEnforced,
    long SmallModelTargetDecisions,
    long FallbackToDefaultCount,
    long BucketSmallDisabled,
    long BucketDefaultProviderDefault,
    long BucketDefaultDeep,
    string LastRouterMode,
    string LastModelTarget,
    string LastReasoningTarget,
    string LastDecisionBucket,
    bool LastApplied,
    bool LastFallbackApplied,
    string LastDecisionReason);
