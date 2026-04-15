using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: resolver provider capabilities по LLM configuration.
/// Зачем: AgentRuntime должен работать с capability profile и оставаться расширяемым для OpenAI, Moonshot и других OpenAI-compatible backend.
/// Как: известные providers получают явный profile, а неизвестные OpenAI-compatible providers получают conservative defaults без provider-specific request patches.
/// </summary>
public sealed class LlmProviderCapabilitiesResolver
{
    public LlmProviderCapabilities Resolve(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (IsMoonshot(options))
        {
            return new LlmProviderCapabilities(
                ProviderKey: "moonshot",
                SupportsTools: true,
                SupportsStreaming: true,
                SupportsReasoning: true,
                RequiresReasoningContentRoundTripForToolCalls: true,
                SupportsReasoningContentRoundTrip: true,
                SupportsExplicitThinkingEnable: false,
                ThinkingControlStyle: LlmThinkingControlStyle.OpenAiCompatibleThinkingObject);
        }

        if (IsOpenAI(options))
        {
            return new LlmProviderCapabilities(
                ProviderKey: "openai",
                SupportsTools: true,
                SupportsStreaming: true,
                SupportsReasoning: false,
                RequiresReasoningContentRoundTripForToolCalls: false,
                SupportsReasoningContentRoundTrip: false,
                SupportsExplicitThinkingEnable: false,
                ThinkingControlStyle: LlmThinkingControlStyle.None);
        }

        return new LlmProviderCapabilities(
            ProviderKey: string.IsNullOrWhiteSpace(options.Provider)
                ? "openai-compatible"
                : options.Provider.Trim().ToLowerInvariant(),
            SupportsTools: true,
            SupportsStreaming: false,
            SupportsReasoning: false,
            RequiresReasoningContentRoundTripForToolCalls: false,
            SupportsReasoningContentRoundTrip: false,
            SupportsExplicitThinkingEnable: false,
            ThinkingControlStyle: LlmThinkingControlStyle.None);
    }

    private static bool IsMoonshot(LlmOptions options) =>
        string.Equals(options.Provider, "moonshot", StringComparison.OrdinalIgnoreCase)
        || options.BaseUrl.Contains("moonshot.ai", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenAI(LlmOptions options) =>
        string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase)
        || options.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
}
