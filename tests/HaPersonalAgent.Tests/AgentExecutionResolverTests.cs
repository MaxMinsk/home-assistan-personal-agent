using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: unit-тесты execution resolver после декомпозиции AgentRuntime.
/// Зачем: нужно отдельно валидировать связку router+planner без участия полного runtime orchestration.
/// Как: проверяет shadow/enforced path и fallback plan recompute на детерминированных входах.
/// </summary>
public class AgentExecutionResolverTests
{
    [Fact]
    public void Resolve_enforced_small_path_selects_small_model_and_disabled_thinking_override()
    {
        var options = new LlmOptions
        {
            Provider = "moonshot",
            BaseUrl = "https://api.moonshot.ai/v1",
            Model = "kimi-k2.5",
            ThinkingMode = LlmThinkingModes.Auto,
            RouterMode = LlmRouterModes.Enforced,
            RouterSmallModel = "moonshot-v1-8k",
            RouterMaxInputCharsForSmall = 1800,
            RouterMaxHistoryMessagesForSmall = 10,
        };
        var context = AgentContext.Create(
            conversationMessages:
            [
                new AgentConversationMessage(AgentConversationRole.User, "Привет", DateTimeOffset.UtcNow),
            ]);
        var resolver = new AgentExecutionResolver(
            new LlmExecutionRouter(),
            new LlmExecutionPlanner(new LlmProviderCapabilitiesResolver()));

        var decision = resolver.Resolve(
            options,
            context,
            "включи свет");

        Assert.True(decision.RoutingDecision.IsApplied);
        Assert.Equal("moonshot-v1-8k", decision.SelectedModel);
        Assert.Equal(LlmThinkingModes.Disabled, decision.SelectedThinkingModeOverride);
        Assert.Equal(LlmEffectiveThinkingMode.Disabled, decision.SelectedPlan.EffectiveThinkingMode);
    }

    [Fact]
    public void Resolve_shadow_mode_keeps_default_model_and_no_override()
    {
        var options = new LlmOptions
        {
            Provider = "moonshot",
            BaseUrl = "https://api.moonshot.ai/v1",
            Model = "kimi-k2.5",
            ThinkingMode = LlmThinkingModes.Auto,
            RouterMode = LlmRouterModes.Shadow,
            RouterSmallModel = "moonshot-v1-8k",
        };
        var resolver = new AgentExecutionResolver(
            new LlmExecutionRouter(),
            new LlmExecutionPlanner(new LlmProviderCapabilitiesResolver()));

        var decision = resolver.Resolve(
            options,
            AgentContext.Create(),
            "сколько сейчас времени");

        Assert.False(decision.RoutingDecision.IsApplied);
        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Null(decision.SelectedThinkingModeOverride);
        Assert.Equal(LlmEffectiveThinkingMode.ProviderDefault, decision.SelectedPlan.EffectiveThinkingMode);
    }

    [Fact]
    public void BuildFallbackPlan_preserves_selected_thinking_override()
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
        var resolver = new AgentExecutionResolver(
            new LlmExecutionRouter(),
            new LlmExecutionPlanner(new LlmProviderCapabilitiesResolver()));
        var decision = resolver.Resolve(
            options,
            AgentContext.Create(),
            "какая погода");

        var fallbackPlan = resolver.BuildFallbackPlan(decision);

        Assert.Equal(LlmThinkingModes.Disabled, decision.SelectedThinkingModeOverride);
        Assert.Equal(LlmEffectiveThinkingMode.Disabled, fallbackPlan.EffectiveThinkingMode);
    }
}
