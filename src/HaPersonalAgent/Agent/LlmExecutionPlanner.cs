using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: planner effective reasoning/thinking режима для LLM provider.
/// Зачем: tool calling, pure chat и deep reasoning требуют разных compromises между стабильностью, reasoning и provider-specific metadata.
/// Как: по LlmOptions, execution profile и capabilities выбирает Disabled/Enabled/ProviderDefault и объясняет решение для logs/status diagnostics.
/// </summary>
public sealed class LlmExecutionPlanner
{
    private readonly LlmProviderCapabilitiesResolver _capabilitiesResolver;

    public LlmExecutionPlanner(LlmProviderCapabilitiesResolver capabilitiesResolver)
    {
        _capabilitiesResolver = capabilitiesResolver;
    }

    public LlmExecutionPlan CreatePlan(
        LlmOptions options,
        LlmExecutionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(options);

        var capabilities = _capabilitiesResolver.Resolve(options);
        var requestedThinkingMode = LlmThinkingModes.Normalize(options.ThinkingMode);

        return requestedThinkingMode switch
        {
            LlmThinkingModes.Disabled => CreateDisabledPlan(profile, capabilities, requestedThinkingMode),
            LlmThinkingModes.Enabled => CreateEnabledPlan(profile, capabilities, requestedThinkingMode),
            _ => CreateAutoPlan(profile, capabilities, requestedThinkingMode),
        };
    }

    private static LlmExecutionPlan CreateAutoPlan(
        LlmExecutionProfile profile,
        LlmProviderCapabilities capabilities,
        string requestedThinkingMode)
    {
        if (profile == LlmExecutionProfile.ToolEnabled
            && capabilities.RequiresReasoningContentRoundTripForToolCalls
            && !capabilities.SupportsReasoningContentRoundTrip
            && capabilities.ThinkingControlStyle != LlmThinkingControlStyle.None)
        {
            return new LlmExecutionPlan(
                profile,
                capabilities,
                requestedThinkingMode,
                LlmEffectiveThinkingMode.Disabled,
                "auto: provider requires reasoning_content round-trip for tool calls, but current adapter cannot preserve it.");
        }

        if (capabilities.SupportsReasoning
            && profile is LlmExecutionProfile.PureChat or LlmExecutionProfile.DeepReasoning)
        {
            return new LlmExecutionPlan(
                profile,
                capabilities,
                requestedThinkingMode,
                LlmEffectiveThinkingMode.ProviderDefault,
                "auto: no tools are available in this profile, so provider reasoning default is allowed.");
        }

        return new LlmExecutionPlan(
            profile,
            capabilities,
            requestedThinkingMode,
            LlmEffectiveThinkingMode.ProviderDefault,
            "auto: no provider-specific thinking override is required.");
    }

    private static LlmExecutionPlan CreateDisabledPlan(
        LlmExecutionProfile profile,
        LlmProviderCapabilities capabilities,
        string requestedThinkingMode)
    {
        if (capabilities.ThinkingControlStyle == LlmThinkingControlStyle.None)
        {
            return new LlmExecutionPlan(
                profile,
                capabilities,
                requestedThinkingMode,
                LlmEffectiveThinkingMode.ProviderDefault,
                "disabled requested, but provider profile has no known request-level thinking control.");
        }

        return new LlmExecutionPlan(
            profile,
            capabilities,
            requestedThinkingMode,
            LlmEffectiveThinkingMode.Disabled,
            "disabled requested by configuration.");
    }

    private static LlmExecutionPlan CreateEnabledPlan(
        LlmExecutionProfile profile,
        LlmProviderCapabilities capabilities,
        string requestedThinkingMode)
    {
        if (!capabilities.SupportsReasoning)
        {
            return new LlmExecutionPlan(
                profile,
                capabilities,
                requestedThinkingMode,
                LlmEffectiveThinkingMode.ProviderDefault,
                "enabled requested, but provider profile does not advertise explicit reasoning support.");
        }

        if (capabilities.ThinkingControlStyle == LlmThinkingControlStyle.None)
        {
            return new LlmExecutionPlan(
                profile,
                capabilities,
                requestedThinkingMode,
                LlmEffectiveThinkingMode.ProviderDefault,
                "enabled requested, but provider profile has no known request-level thinking control.");
        }

        if (!capabilities.SupportsExplicitThinkingEnable)
        {
            return new LlmExecutionPlan(
                profile,
                capabilities,
                requestedThinkingMode,
                LlmEffectiveThinkingMode.ProviderDefault,
                profile == LlmExecutionProfile.ToolEnabled
                    && capabilities.RequiresReasoningContentRoundTripForToolCalls
                    && !capabilities.SupportsReasoningContentRoundTrip
                    ? "enabled requested explicitly; provider default is allowed, but tool-call reasoning_content round-trip is not guaranteed."
                    : "enabled requested; provider default is used because explicit enable is not configured for this provider profile.");
        }

        return new LlmExecutionPlan(
            profile,
            capabilities,
            requestedThinkingMode,
            LlmEffectiveThinkingMode.Enabled,
            profile == LlmExecutionProfile.ToolEnabled
                && capabilities.RequiresReasoningContentRoundTripForToolCalls
                && !capabilities.SupportsReasoningContentRoundTrip
                ? "enabled requested explicitly; tool-call reasoning_content round-trip is not guaranteed."
                : "enabled requested by configuration.");
    }
}
