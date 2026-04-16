using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: unit-тесты fallback executor для routed small-model path.
/// Зачем: нужно гарантировать, что retry-политика остается изолированной и предсказуемой после декомпозиции runtime.
/// Как: проверяет позитивный сценарий retryable status и негативный сценарий non-retryable статуса.
/// </summary>
public class AgentFallbackExecutorTests
{
    [Fact]
    public void TryCreateFallback_returns_default_model_for_retryable_small_path()
    {
        var decision = CreateSmallPathDecision();
        var executor = new AgentFallbackExecutor();

        var canFallback = executor.TryCreateFallback(
            decision,
            providerStatusCode: 429,
            out var fallbackContext);

        Assert.True(canFallback);
        Assert.True(fallbackContext.IsEnabled);
        Assert.Equal("kimi-k2.5", fallbackContext.FallbackModel);
    }

    [Fact]
    public void TryCreateFallback_returns_false_for_non_retryable_status()
    {
        var decision = CreateSmallPathDecision();
        var executor = new AgentFallbackExecutor();

        var canFallback = executor.TryCreateFallback(
            decision,
            providerStatusCode: 401,
            out var fallbackContext);

        Assert.False(canFallback);
        Assert.False(fallbackContext.IsEnabled);
        Assert.Null(fallbackContext.FallbackModel);
    }

    private static AgentExecutionDecision CreateSmallPathDecision()
    {
        var options = new LlmOptions
        {
            Provider = "moonshot",
            BaseUrl = "https://api.moonshot.ai/v1",
            Model = "kimi-k2.5",
            ThinkingMode = LlmThinkingModes.Auto,
            RouterMode = LlmRouterModes.Enforced,
            RouterSmallModel = "moonshot-v1-8k",
        };
        var context = AgentContext.Create();
        var routingDecision = new LlmRoutingDecision(
            RouterMode: LlmRouterModes.Enforced,
            IsApplied: true,
            ModelTarget: LlmRoutingDecision.ModelTargetSmallContextFast,
            SelectedModel: "moonshot-v1-8k",
            ReasoningTarget: LlmRoutingDecision.ReasoningTargetDisabled,
            ThinkingModeOverride: LlmThinkingModes.Disabled,
            DecisionBucket: LlmRoutingDecision.DecisionBucketSmallDisabled,
            Reason: "test",
            EstimatedInputChars: 100,
            HistoryMessageCount: 2);
        var plan = new LlmExecutionPlanner(new LlmProviderCapabilitiesResolver()).CreatePlan(
            options,
            LlmExecutionProfile.ToolEnabled,
            LlmThinkingModes.Disabled);

        return new AgentExecutionDecision(
            options,
            context,
            UserMessage: "включи свет",
            DefaultModel: "kimi-k2.5",
            RoutingDecision: routingDecision,
            SelectedModel: "moonshot-v1-8k",
            SelectedThinkingModeOverride: LlmThinkingModes.Disabled,
            SelectedPlan: plan);
    }
}
