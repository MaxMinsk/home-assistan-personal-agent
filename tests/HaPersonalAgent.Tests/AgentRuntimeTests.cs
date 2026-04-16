using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты первого MAF runtime spike без реального сетевого вызова к LLM.
/// Зачем: важно проверить поведение без API key и безопасность status tool до подключения Telegram.
/// Как: создает runtime на Options.Create и пустом ServiceProvider, а status проверяет на отсутствие сырых секретов.
/// </summary>
public class AgentRuntimeTests
{
    [Fact]
    public async Task Runtime_reports_not_configured_without_llm_api_key()
    {
        var runtime = CreateRuntime(new LlmOptions
        {
            ApiKey = "",
        });

        var health = runtime.GetHealth();
        var response = await runtime.SendAsync(
            "hello",
            AgentContext.Create("test-correlation"),
            onReasoningUpdate: null,
            CancellationToken.None);

        Assert.False(health.IsConfigured);
        Assert.False(response.IsConfigured);
        Assert.Equal("test-correlation", response.CorrelationId);
        Assert.Contains("not configured", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_reports_not_configured_with_invalid_thinking_mode()
    {
        var runtime = CreateRuntime(new LlmOptions
        {
            ApiKey = "configured",
            ThinkingMode = "always",
        });

        var health = runtime.GetHealth();

        Assert.False(health.IsConfigured);
        Assert.Contains("ThinkingMode", health.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_tool_returns_safe_status_without_raw_secrets()
    {
        var statusProvider = CreateConfigurationStatusProvider(
            new LlmOptions
            {
                ApiKey = "moonshot-secret",
            },
            new TelegramOptions
            {
                BotToken = "telegram-secret",
                AllowedUserIds = new long[] { 123 },
            },
            new HomeAssistantOptions
            {
                LongLivedAccessToken = "ha-secret",
            },
            new AgentOptions());
        var tool = new AgentStatusTool(statusProvider);

        var status = tool.GetStatus();
        var statusText = status.ToString();

        Assert.Equal(ApplicationInfo.Name, status.ApplicationName);
        Assert.Equal(ApplicationInfo.TargetFramework, status.TargetFramework);
        Assert.Equal("local", status.ConfigurationMode);
        Assert.DoesNotContain("moonshot-secret", statusText);
        Assert.DoesNotContain("telegram-secret", statusText);
        Assert.DoesNotContain("ha-secret", statusText);
    }

    [Fact]
    public void Planner_uses_provider_default_for_moonshot_tool_enabled_auto_when_roundtrip_supported()
    {
        var plan = CreatePlanner().CreatePlan(
            new LlmOptions
            {
                Provider = "moonshot",
                BaseUrl = "https://api.moonshot.ai/v1",
                ThinkingMode = LlmThinkingModes.Auto,
            },
            LlmExecutionProfile.ToolEnabled);
        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            """
            {"model":"kimi-k2.5","messages":[]}
            """,
            plan,
            out _);

        Assert.Equal(LlmEffectiveThinkingMode.ProviderDefault, plan.EffectiveThinkingMode);
        Assert.False(patched);
    }

    [Fact]
    public void Planner_does_not_force_disabled_thinking_for_moonshot_pure_chat_auto()
    {
        var plan = CreatePlanner().CreatePlan(
            new LlmOptions
            {
                Provider = "moonshot",
                BaseUrl = "https://api.moonshot.ai/v1",
                ThinkingMode = LlmThinkingModes.Auto,
            },
            LlmExecutionProfile.PureChat);

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            """
            {"model":"kimi-k2.5","messages":[]}
            """,
            plan,
            out _);

        Assert.Equal(LlmEffectiveThinkingMode.ProviderDefault, plan.EffectiveThinkingMode);
        Assert.False(patched);
    }

    [Fact]
    public void Planner_forces_disabled_thinking_for_moonshot_summarization_profile()
    {
        var plan = CreatePlanner().CreatePlan(
            new LlmOptions
            {
                Provider = "moonshot",
                BaseUrl = "https://api.moonshot.ai/v1",
                ThinkingMode = LlmThinkingModes.Auto,
            },
            LlmExecutionProfile.Summarization);

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            """
            {"model":"kimi-k2.5","messages":[]}
            """,
            plan,
            out var patchedJson);

        using var document = JsonDocument.Parse(patchedJson);

        Assert.Equal(LlmEffectiveThinkingMode.Disabled, plan.EffectiveThinkingMode);
        Assert.True(patched);
        Assert.Equal("disabled", document.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public void Policy_disables_thinking_for_moonshot_auto_when_tool_call_history_has_no_reasoning_content()
    {
        var plan = CreatePlanner().CreatePlan(
            new LlmOptions
            {
                Provider = "moonshot",
                BaseUrl = "https://api.moonshot.ai/v1",
                ThinkingMode = LlmThinkingModes.Auto,
            },
            LlmExecutionProfile.ToolEnabled);

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            """
            {
              "model":"kimi-k2.5",
              "messages":[
                {"role":"user","content":"сколько градусов?"},
                {
                  "role":"assistant",
                  "tool_calls":[
                    {
                      "id":"call_1",
                      "type":"function",
                      "function":{"name":"GetLiveContext","arguments":"{}"}
                    }
                  ]
                },
                {"role":"tool","tool_call_id":"call_1","content":"{\"temperature\":21}"}
              ]
            }
            """,
            plan,
            out var patchedJson);

        using var document = JsonDocument.Parse(patchedJson);

        Assert.Equal(LlmEffectiveThinkingMode.ProviderDefault, plan.EffectiveThinkingMode);
        Assert.True(patched);
        Assert.Equal("disabled", document.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public void Policy_keeps_provider_default_for_moonshot_auto_when_tool_call_history_has_reasoning_content()
    {
        var plan = CreatePlanner().CreatePlan(
            new LlmOptions
            {
                Provider = "moonshot",
                BaseUrl = "https://api.moonshot.ai/v1",
                ThinkingMode = LlmThinkingModes.Auto,
            },
            LlmExecutionProfile.ToolEnabled);

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            """
            {
              "model":"kimi-k2.5",
              "messages":[
                {"role":"user","content":"сколько градусов?"},
                {
                  "role":"assistant",
                  "reasoning_content":"internal reasoning",
                  "tool_calls":[
                    {
                      "id":"call_1",
                      "type":"function",
                      "function":{"name":"GetLiveContext","arguments":"{}"}
                    }
                  ]
                },
                {"role":"tool","tool_call_id":"call_1","content":"{\"temperature\":21}"}
              ]
            }
            """,
            plan,
            out _);

        Assert.Equal(LlmEffectiveThinkingMode.ProviderDefault, plan.EffectiveThinkingMode);
        Assert.False(patched);
    }

    [Fact]
    public void Planner_respects_explicit_disabled_mode_for_moonshot()
    {
        var plan = CreatePlanner().CreatePlan(
            new LlmOptions
            {
                Provider = "moonshot",
                BaseUrl = "https://api.moonshot.ai/v1",
                ThinkingMode = LlmThinkingModes.Disabled,
            },
            LlmExecutionProfile.ToolEnabled);

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            """
            {"model":"kimi-k2.5","messages":[]}
            """,
            plan,
            out var patchedJson);

        using var document = JsonDocument.Parse(patchedJson);

        Assert.Equal(LlmEffectiveThinkingMode.Disabled, plan.EffectiveThinkingMode);
        Assert.True(patched);
        Assert.Equal("disabled", document.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public void Planner_keeps_generic_openai_compatible_provider_unpatched()
    {
        var plan = CreatePlanner().CreatePlan(
            new LlmOptions
            {
                Provider = "openai-compatible",
                BaseUrl = "https://llm.local/v1",
                ThinkingMode = LlmThinkingModes.Disabled,
            },
            LlmExecutionProfile.ToolEnabled);

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            """
            {"model":"local-model","messages":[]}
            """,
            plan,
            out _);

        Assert.Equal("openai-compatible", plan.Capabilities.ProviderKey);
        Assert.Equal(LlmEffectiveThinkingMode.ProviderDefault, plan.EffectiveThinkingMode);
        Assert.False(patched);
    }

    [Fact]
    public void Planner_uses_provider_default_for_moonshot_enabled_deep_reasoning()
    {
        var plan = CreatePlanner().CreatePlan(
            new LlmOptions
            {
                Provider = "moonshot",
                BaseUrl = "https://api.moonshot.ai/v1",
                ThinkingMode = LlmThinkingModes.Enabled,
            },
            LlmExecutionProfile.DeepReasoning);

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            """
            {"model":"kimi-k2.5","messages":[]}
            """,
            plan,
            out _);

        Assert.Equal(LlmEffectiveThinkingMode.ProviderDefault, plan.EffectiveThinkingMode);
        Assert.False(patched);
    }

    [Fact]
    public void Policy_can_emit_enabled_when_provider_profile_supports_explicit_enable()
    {
        var plan = new LlmExecutionPlan(
            LlmExecutionProfile.DeepReasoning,
            new LlmProviderCapabilities(
                ProviderKey: "test-provider",
                SupportsTools: true,
                SupportsStreaming: true,
                SupportsReasoning: true,
                RequiresReasoningContentRoundTripForToolCalls: false,
                SupportsReasoningContentRoundTrip: false,
                SupportsExplicitThinkingEnable: true,
                ThinkingControlStyle: LlmThinkingControlStyle.OpenAiCompatibleThinkingObject),
            LlmThinkingModes.Enabled,
            LlmEffectiveThinkingMode.Enabled,
            "test");

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            """
            {"model":"test-model","messages":[]}
            """,
            plan,
            out var patchedJson);

        using var document = JsonDocument.Parse(patchedJson);

        Assert.True(patched);
        Assert.Equal("enabled", document.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public void Runtime_reports_not_configured_with_invalid_router_mode()
    {
        var runtime = CreateRuntime(new LlmOptions
        {
            ApiKey = "configured",
            RouterMode = "smart-auto",
        });

        var health = runtime.GetHealth();

        Assert.False(health.IsConfigured);
        Assert.Contains("RouterMode", health.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_routes_small_model_for_simple_prompt_in_small_context()
    {
        var router = new LlmExecutionRouter();
        var decision = router.Decide(
            new LlmOptions
            {
                Model = "kimi-k2.5",
                RouterMode = LlmRouterModes.Enforced,
                RouterSmallModel = "moonshot-v1-8k",
                RouterMaxInputCharsForSmall = 1800,
                RouterMaxHistoryMessagesForSmall = 10,
            },
            AgentContext.Create(
                conversationMessages:
                [
                    new AgentConversationMessage(AgentConversationRole.User, "Привет", DateTimeOffset.UtcNow),
                    new AgentConversationMessage(AgentConversationRole.Assistant, "Привет!", DateTimeOffset.UtcNow),
                ]),
            userMessage: "включи свет на кухне",
            profile: LlmExecutionProfile.ToolEnabled);

        Assert.Equal(LlmRouterModes.Enforced, decision.RouterMode);
        Assert.True(decision.IsApplied);
        Assert.Equal(LlmRoutingDecision.ModelTargetSmallContextFast, decision.ModelTarget);
        Assert.Equal("moonshot-v1-8k", decision.SelectedModel);
        Assert.Equal(LlmRoutingDecision.ReasoningTargetDisabled, decision.ReasoningTarget);
        Assert.Equal(LlmThinkingModes.Disabled, decision.ThinkingModeOverride);
        Assert.Equal(LlmRoutingDecision.DecisionBucketSmallDisabled, decision.DecisionBucket);
    }

    [Fact]
    public void Router_keeps_default_model_in_shadow_mode()
    {
        var router = new LlmExecutionRouter();
        var decision = router.Decide(
            new LlmOptions
            {
                Model = "kimi-k2.5",
                RouterMode = LlmRouterModes.Shadow,
                RouterSmallModel = "moonshot-v1-8k",
            },
            AgentContext.Create(),
            userMessage: "сколько сейчас времени",
            profile: LlmExecutionProfile.ToolEnabled);

        Assert.Equal(LlmRouterModes.Shadow, decision.RouterMode);
        Assert.False(decision.IsApplied);
        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Null(decision.ThinkingModeOverride);
    }

    [Fact]
    public void Router_routes_deep_when_keyword_matches()
    {
        var router = new LlmExecutionRouter();
        var decision = router.Decide(
            new LlmOptions
            {
                Model = "kimi-k2.5",
                RouterMode = LlmRouterModes.Enforced,
                RouterDeepKeywords = "глубоко,подумай глубже",
            },
            AgentContext.Create(),
            userMessage: "подумай глубже и распиши план по шагам",
            profile: LlmExecutionProfile.ToolEnabled);

        Assert.True(decision.IsApplied);
        Assert.Equal(LlmRoutingDecision.ModelTargetDefault, decision.ModelTarget);
        Assert.Equal("kimi-k2.5", decision.SelectedModel);
        Assert.Equal(LlmRoutingDecision.ReasoningTargetDeep, decision.ReasoningTarget);
        Assert.Equal(LlmThinkingModes.Enabled, decision.ThinkingModeOverride);
        Assert.Equal(LlmRoutingDecision.DecisionBucketDefaultDeep, decision.DecisionBucket);
    }

    [Fact]
    public void Router_thinking_override_interacts_with_planner()
    {
        var options = new LlmOptions
        {
            Provider = "moonshot",
            BaseUrl = "https://api.moonshot.ai/v1",
            ThinkingMode = LlmThinkingModes.Auto,
            RouterMode = LlmRouterModes.Enforced,
            RouterSmallModel = "moonshot-v1-8k",
        };
        var router = new LlmExecutionRouter();
        var decision = router.Decide(
            options,
            AgentContext.Create(),
            userMessage: "какая погода",
            profile: LlmExecutionProfile.ToolEnabled);

        var plan = CreatePlanner().CreatePlan(
            options,
            LlmExecutionProfile.ToolEnabled,
            decision.ThinkingModeOverride);

        Assert.Equal(LlmThinkingModes.Disabled, decision.ThinkingModeOverride);
        Assert.Equal(LlmEffectiveThinkingMode.Disabled, plan.EffectiveThinkingMode);
    }

    [Fact]
    public void Fallback_policy_retries_small_model_for_retryable_status()
    {
        var decision = new LlmRoutingDecision(
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

        var retry = LlmRoutingFallbackPolicy.CanRetryWithDefaultModel(
            decision,
            selectedModel: "moonshot-v1-8k",
            defaultModel: "kimi-k2.5",
            providerStatusCode: 429);

        Assert.True(retry);
    }

    [Fact]
    public void Fallback_policy_does_not_retry_for_auth_failure()
    {
        var decision = new LlmRoutingDecision(
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

        var retry = LlmRoutingFallbackPolicy.CanRetryWithDefaultModel(
            decision,
            selectedModel: "moonshot-v1-8k",
            defaultModel: "kimi-k2.5",
            providerStatusCode: 401);

        Assert.False(retry);
    }

    private static AgentRuntime CreateRuntime(LlmOptions llmOptions)
    {
        var statusProvider = CreateConfigurationStatusProvider(
            llmOptions,
            new TelegramOptions(),
            new HomeAssistantOptions(),
            new AgentOptions());

        return new AgentRuntime(
            Options.Create(llmOptions),
            new AgentStatusTool(statusProvider),
            CreatePlanner(),
            LoggerFactory.Create(_ => { }),
            serviceProvider: new EmptyServiceProvider());
    }

    private static LlmExecutionPlanner CreatePlanner() =>
        new(new LlmProviderCapabilitiesResolver());

    private static ConfigurationStatusProvider CreateConfigurationStatusProvider(
        LlmOptions llmOptions,
        TelegramOptions telegramOptions,
        HomeAssistantOptions homeAssistantOptions,
        AgentOptions agentOptions) =>
        new(
            Options.Create(agentOptions),
            Options.Create(telegramOptions),
            Options.Create(llmOptions),
            Options.Create(homeAssistantOptions));

    /// <summary>
    /// Что: минимальная заглушка IServiceProvider для unit-тестов runtime.
    /// Зачем: AgentRuntime ожидает service provider для MAF tools, но эти тесты не должны поднимать настоящий DI container.
    /// Как: всегда возвращает null, потому что проверяем сценарии, где tool dependencies уже переданы напрямую.
    /// </summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
