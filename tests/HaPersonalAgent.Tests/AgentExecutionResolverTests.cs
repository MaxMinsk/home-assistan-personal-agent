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
            RouterSimpleMaxInputChars = 3200,
            RouterSimpleMaxHistoryMessages = 4,
            RouterSimpleAllowTools = false,
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
            "ну шо, как жизнь?");

        Assert.True(decision.RoutingDecision.IsApplied);
        Assert.Equal("moonshot-v1-8k", decision.SelectedModel);
        Assert.Equal(LlmThinkingModes.Disabled, decision.SelectedThinkingModeOverride);
        Assert.Equal(LlmEffectiveThinkingMode.Disabled, decision.SelectedPlan.EffectiveThinkingMode);
        Assert.Equal(LlmRoutingDecision.ContextProfileSimplePacked, decision.EffectiveContextProfile);
        Assert.Equal(LlmExecutionProfile.PureChat, decision.EffectiveContext.ExecutionProfile);
        Assert.True(decision.EffectiveContext.ConversationMessages.Count <= 4);
        Assert.Equal(0, decision.EffectiveContext.RetrievedMemoryCount);
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
            "ну шо, ты на месте?");

        Assert.False(decision.RoutingDecision.IsApplied);
        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Null(decision.SelectedThinkingModeOverride);
        Assert.Equal(LlmEffectiveThinkingMode.ProviderDefault, decision.SelectedPlan.EffectiveThinkingMode);
        Assert.Equal(LlmRoutingDecision.ContextProfileDefaultFull, decision.EffectiveContextProfile);
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
            "привет, как дела?");

        var fallbackPlan = resolver.BuildFallbackPlan(decision);

        Assert.Equal(LlmThinkingModes.Disabled, decision.SelectedThinkingModeOverride);
        Assert.Equal(LlmEffectiveThinkingMode.Disabled, fallbackPlan.EffectiveThinkingMode);
    }

    [Fact]
    public void Resolve_enforced_tool_heavy_prompt_keeps_default_model_and_full_context_profile()
    {
        var options = new LlmOptions
        {
            Provider = "moonshot",
            BaseUrl = "https://api.moonshot.ai/v1",
            Model = "kimi-k2.5",
            ThinkingMode = LlmThinkingModes.Auto,
            RouterMode = LlmRouterModes.Enforced,
            RouterSmallModel = "moonshot-v1-8k",
            RouterSimpleAllowTools = false,
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
            "включи свет в спальне");

        Assert.True(decision.RoutingDecision.IsApplied);
        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Equal(LlmRoutingDecision.IntentClassToolHeavy, decision.RoutingDecision.IntentClass);
        Assert.Equal(LlmRoutingDecision.ContextProfileDefaultFull, decision.EffectiveContextProfile);
        Assert.Equal(context.ExecutionProfile, decision.EffectiveContext.ExecutionProfile);
    }
}
