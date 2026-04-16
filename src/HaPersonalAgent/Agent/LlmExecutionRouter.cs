using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: детерминированный router выбора модели и reasoning-режима.
/// Зачем: HAAG-048 снижает latency/стоимость без отдельного LLM-классификатора: решение принимается из локальных features запроса и контекста.
/// Как: строит LlmRoutingDecision для off/shadow/enforced режима; в shadow решение только логируется, в enforced применяется в runtime.
/// </summary>
public sealed class LlmExecutionRouter
{
    private static readonly char[] KeywordSeparators = [',', ';', '\n', '\r', '\t', '|'];
    private static readonly string[] DefaultDeepIntentKeywords =
    [
        "пошагово",
        "по шагам",
        "step-by-step",
        "step by step",
        "deep reasoning",
        "подумай глубже",
    ];

    private static readonly string[] ComplexIntentMarkers =
    [
        "анализ",
        "сравни",
        "план",
        "стратег",
        "архитект",
        "workflow",
        "design",
        "tradeoff",
        "почему",
        "объясни",
    ];

    private static readonly string[] ToolIntentMarkers =
    [
        "включи",
        "выключи",
        "turn on",
        "turn off",
        "mcp",
        "home assistant",
        "датчик",
        "сенсор",
        "температур",
        "свет",
        "таймер",
        "камера",
        "frigate",
        "todo",
        "список",
        "капсул",
        "project capsule",
        "approve",
        "reject",
        "/approve",
        "/reject",
        "hass",
    ];

    private readonly LlmRoutingContextProfileBuilder _contextProfileBuilder;

    public LlmExecutionRouter(LlmRoutingContextProfileBuilder? contextProfileBuilder = null)
    {
        _contextProfileBuilder = contextProfileBuilder ?? new LlmRoutingContextProfileBuilder();
    }

    public LlmRoutingDecision Decide(
        LlmOptions options,
        AgentContext context,
        string userMessage,
        LlmExecutionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        var routerMode = LlmRouterModes.Normalize(options.RouterMode);
        var normalizedDefaultModel = NormalizeModel(options.Model, fallback: "unknown-model");
        var normalizedSmallModel = NormalizeModel(options.RouterSmallModel, normalizedDefaultModel);
        var normalizedMessage = userMessage.Trim();
        var simpleMaxChars = Math.Clamp(options.RouterSimpleMaxInputChars, 400, 24_000);
        var simpleMaxHistory = Math.Clamp(options.RouterSimpleMaxHistoryMessages, 2, 64);
        var deepKeywords = ParseKeywords(options.RouterDeepKeywords);

        var defaultProfile = _contextProfileBuilder.BuildDefault(context, normalizedMessage);
        var simplePackedProfile = _contextProfileBuilder.BuildSimplePacked(context, normalizedMessage, options);
        var intentClass = ClassifyIntent(profile, normalizedMessage, deepKeywords);
        var isSimplePromptShape = IsSimplePromptShape(normalizedMessage);
        var fitsSimplePackedContext = simplePackedProfile.EstimatedInputChars <= simpleMaxChars
            && simplePackedProfile.HistoryMessageCount <= simpleMaxHistory;
        var isSmallEligibleProfile = profile is LlmExecutionProfile.ToolEnabled or LlmExecutionProfile.PureChat;

        // Extension point: на следующем этапе сюда можно добавить feature flags:
        // 1) provider-specific budgets (tokens/$ per model),
        // 2) lightweight lexical classifier per domain,
        // 3) per-user calibration из исторической telemetry.
        var candidateDecision = SelectCandidateDecision(
            normalizedDefaultModel,
            normalizedSmallModel,
            intentClass,
            isSimplePromptShape,
            fitsSimplePackedContext,
            isSmallEligibleProfile,
            defaultProfile,
            simplePackedProfile);

        var isApplied = string.Equals(routerMode, LlmRouterModes.Enforced, StringComparison.Ordinal);
        var selectedModel = isApplied
            ? candidateDecision.SelectedModel
            : normalizedDefaultModel;
        var thinkingModeOverride = isApplied
            ? candidateDecision.ThinkingModeOverride
            : null;

        var reason = BuildReason(
            routerMode,
            candidateDecision.Reason,
            intentClass,
            candidateDecision.ContextProfile,
            candidateDecision.ContextProfileBlockerReason,
            defaultProfile,
            simplePackedProfile,
            simpleMaxChars,
            simpleMaxHistory);

        return candidateDecision with
        {
            RouterMode = routerMode,
            IsApplied = isApplied,
            SelectedModel = selectedModel,
            ThinkingModeOverride = thinkingModeOverride,
            Reason = reason,
        };
    }

    private static LlmRoutingDecision SelectCandidateDecision(
        string defaultModel,
        string smallModel,
        string intentClass,
        bool isSimplePromptShape,
        bool fitsSimplePackedContext,
        bool isSmallEligibleProfile,
        LlmRoutingContextProfile defaultProfile,
        LlmRoutingContextProfile simplePackedProfile)
    {
        if (string.Equals(intentClass, LlmRoutingDecision.IntentClassDeepReasoning, StringComparison.Ordinal))
        {
            return new LlmRoutingDecision(
                RouterMode: LlmRouterModes.Off,
                IsApplied: false,
                ModelTarget: LlmRoutingDecision.ModelTargetDefault,
                SelectedModel: defaultModel,
                ReasoningTarget: LlmRoutingDecision.ReasoningTargetDeep,
                ThinkingModeOverride: LlmThinkingModes.Enabled,
                DecisionBucket: LlmRoutingDecision.DecisionBucketDefaultDeep,
                IntentClass: intentClass,
                ContextProfile: LlmRoutingDecision.ContextProfileDefaultFull,
                ContextProfileBlockerReason: null,
                Reason: "deep intent marker detected in request/profile.",
                EstimatedInputChars: defaultProfile.EstimatedInputChars,
                HistoryMessageCount: defaultProfile.HistoryMessageCount);
        }

        if (string.Equals(intentClass, LlmRoutingDecision.IntentClassToolHeavy, StringComparison.Ordinal))
        {
            return new LlmRoutingDecision(
                RouterMode: LlmRouterModes.Off,
                IsApplied: false,
                ModelTarget: LlmRoutingDecision.ModelTargetDefault,
                SelectedModel: defaultModel,
                ReasoningTarget: LlmRoutingDecision.ReasoningTargetProviderDefault,
                ThinkingModeOverride: null,
                DecisionBucket: LlmRoutingDecision.DecisionBucketDefaultProviderDefault,
                IntentClass: intentClass,
                ContextProfile: LlmRoutingDecision.ContextProfileDefaultFull,
                ContextProfileBlockerReason: "tool intent requires full tool-enabled context profile.",
                Reason: "tool-heavy intent detected; keep default model/profile.",
                EstimatedInputChars: defaultProfile.EstimatedInputChars,
                HistoryMessageCount: defaultProfile.HistoryMessageCount);
        }

        if (string.Equals(intentClass, LlmRoutingDecision.IntentClassSimpleChat, StringComparison.Ordinal))
        {
            if (!isSmallEligibleProfile)
            {
                return new LlmRoutingDecision(
                    RouterMode: LlmRouterModes.Off,
                    IsApplied: false,
                    ModelTarget: LlmRoutingDecision.ModelTargetDefault,
                    SelectedModel: defaultModel,
                    ReasoningTarget: LlmRoutingDecision.ReasoningTargetProviderDefault,
                    ThinkingModeOverride: null,
                    DecisionBucket: LlmRoutingDecision.DecisionBucketDefaultProviderDefault,
                    IntentClass: intentClass,
                    ContextProfile: LlmRoutingDecision.ContextProfileDefaultFull,
                    ContextProfileBlockerReason: "execution profile is not eligible for small-path routing.",
                    Reason: "simple intent detected but execution profile is not small-route eligible.",
                    EstimatedInputChars: defaultProfile.EstimatedInputChars,
                    HistoryMessageCount: defaultProfile.HistoryMessageCount);
            }

            if (!isSimplePromptShape)
            {
                return new LlmRoutingDecision(
                    RouterMode: LlmRouterModes.Off,
                    IsApplied: false,
                    ModelTarget: LlmRoutingDecision.ModelTargetDefault,
                    SelectedModel: defaultModel,
                    ReasoningTarget: LlmRoutingDecision.ReasoningTargetProviderDefault,
                    ThinkingModeOverride: null,
                    DecisionBucket: LlmRoutingDecision.DecisionBucketDefaultProviderDefault,
                    IntentClass: intentClass,
                    ContextProfile: LlmRoutingDecision.ContextProfileDefaultFull,
                    ContextProfileBlockerReason: "simple intent exceeds prompt-shape guardrails (too long/multiline).",
                    Reason: "simple intent detected but prompt shape is too large for deterministic small-path.",
                    EstimatedInputChars: defaultProfile.EstimatedInputChars,
                    HistoryMessageCount: defaultProfile.HistoryMessageCount);
            }

            if (!fitsSimplePackedContext)
            {
                return new LlmRoutingDecision(
                    RouterMode: LlmRouterModes.Off,
                    IsApplied: false,
                    ModelTarget: LlmRoutingDecision.ModelTargetDefault,
                    SelectedModel: defaultModel,
                    ReasoningTarget: LlmRoutingDecision.ReasoningTargetProviderDefault,
                    ThinkingModeOverride: null,
                    DecisionBucket: LlmRoutingDecision.DecisionBucketDefaultProviderDefault,
                    IntentClass: intentClass,
                    ContextProfile: LlmRoutingDecision.ContextProfileDefaultFull,
                    ContextProfileBlockerReason: "simple packed context exceeds configured budget.",
                    Reason: "simple intent detected but packed context does not fit simple-path budget.",
                    EstimatedInputChars: defaultProfile.EstimatedInputChars,
                    HistoryMessageCount: defaultProfile.HistoryMessageCount);
            }

            return new LlmRoutingDecision(
                RouterMode: LlmRouterModes.Off,
                IsApplied: false,
                ModelTarget: LlmRoutingDecision.ModelTargetSmallContextFast,
                SelectedModel: smallModel,
                ReasoningTarget: LlmRoutingDecision.ReasoningTargetDisabled,
                ThinkingModeOverride: LlmThinkingModes.Disabled,
                DecisionBucket: LlmRoutingDecision.DecisionBucketSmallDisabled,
                IntentClass: intentClass,
                ContextProfile: LlmRoutingDecision.ContextProfileSimplePacked,
                ContextProfileBlockerReason: null,
                Reason: "simple-chat intent with packed context inside simple-route budget.",
                EstimatedInputChars: simplePackedProfile.EstimatedInputChars,
                HistoryMessageCount: simplePackedProfile.HistoryMessageCount);
        }

        if (string.Equals(intentClass, LlmRoutingDecision.IntentClassComplexAnalysis, StringComparison.Ordinal))
        {
            return new LlmRoutingDecision(
                RouterMode: LlmRouterModes.Off,
                IsApplied: false,
                ModelTarget: LlmRoutingDecision.ModelTargetDefault,
                SelectedModel: defaultModel,
                ReasoningTarget: LlmRoutingDecision.ReasoningTargetProviderDefault,
                ThinkingModeOverride: null,
                DecisionBucket: LlmRoutingDecision.DecisionBucketDefaultProviderDefault,
                IntentClass: intentClass,
                ContextProfile: LlmRoutingDecision.ContextProfileDefaultFull,
                ContextProfileBlockerReason: null,
                Reason: "complex-analysis intent detected; keep default model/profile.",
                EstimatedInputChars: defaultProfile.EstimatedInputChars,
                HistoryMessageCount: defaultProfile.HistoryMessageCount);
        }

        if (isSmallEligibleProfile && fitsSimplePackedContext && isSimplePromptShape)
        {
            return new LlmRoutingDecision(
                RouterMode: LlmRouterModes.Off,
                IsApplied: false,
                ModelTarget: LlmRoutingDecision.ModelTargetSmallContextFast,
                SelectedModel: smallModel,
                ReasoningTarget: LlmRoutingDecision.ReasoningTargetDisabled,
                ThinkingModeOverride: LlmThinkingModes.Disabled,
                DecisionBucket: LlmRoutingDecision.DecisionBucketSmallDisabled,
                IntentClass: LlmRoutingDecision.IntentClassSimpleChat,
                ContextProfile: LlmRoutingDecision.ContextProfileSimplePacked,
                ContextProfileBlockerReason: null,
                Reason: "fallback simple-route path selected.",
                EstimatedInputChars: simplePackedProfile.EstimatedInputChars,
                HistoryMessageCount: simplePackedProfile.HistoryMessageCount);
        }

        return new LlmRoutingDecision(
            RouterMode: LlmRouterModes.Off,
            IsApplied: false,
            ModelTarget: LlmRoutingDecision.ModelTargetDefault,
            SelectedModel: defaultModel,
            ReasoningTarget: LlmRoutingDecision.ReasoningTargetProviderDefault,
            ThinkingModeOverride: null,
            DecisionBucket: LlmRoutingDecision.DecisionBucketDefaultProviderDefault,
            IntentClass: LlmRoutingDecision.IntentClassComplexAnalysis,
            ContextProfile: LlmRoutingDecision.ContextProfileDefaultFull,
            ContextProfileBlockerReason: "intent classification fallback selected default profile.",
            Reason: "default path keeps provider model and reasoning mode.",
            EstimatedInputChars: defaultProfile.EstimatedInputChars,
            HistoryMessageCount: defaultProfile.HistoryMessageCount);
    }

    private static string ClassifyIntent(
        LlmExecutionProfile profile,
        string message,
        IReadOnlyList<string> deepKeywords)
    {
        if (HasDeepIntent(profile, message, deepKeywords))
        {
            return LlmRoutingDecision.IntentClassDeepReasoning;
        }

        if (HasToolIntent(message))
        {
            return LlmRoutingDecision.IntentClassToolHeavy;
        }

        if (IsComplexIntent(message))
        {
            return LlmRoutingDecision.IntentClassComplexAnalysis;
        }

        return LlmRoutingDecision.IntentClassSimpleChat;
    }

    private static bool HasDeepIntent(
        LlmExecutionProfile profile,
        string message,
        IReadOnlyList<string> deepKeywords)
    {
        if (profile == LlmExecutionProfile.DeepReasoning)
        {
            return true;
        }

        foreach (var keyword in deepKeywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ParseKeywords(string? rawKeywords)
    {
        if (string.IsNullOrWhiteSpace(rawKeywords))
        {
            return DefaultDeepIntentKeywords;
        }

        var userKeywords = rawKeywords
            .Split(KeywordSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(keyword => keyword.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return userKeywords.Length > 0
            ? userKeywords
            : DefaultDeepIntentKeywords;
    }

    private static bool IsSimplePromptShape(string message)
    {
        if (message.Length > 420)
        {
            return false;
        }

        if (message.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        var tokenCount = message.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
        if (tokenCount > 72)
        {
            return false;
        }

        return true;
    }

    private static bool IsComplexIntent(string message)
    {
        foreach (var marker in ComplexIntentMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasToolIntent(string message)
    {
        foreach (var marker in ToolIntentMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeModel(string? value, string fallback)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? fallback
            : trimmed;
    }

    private static string BuildReason(
        string routerMode,
        string selectedReason,
        string intentClass,
        string contextProfile,
        string? contextProfileBlockerReason,
        LlmRoutingContextProfile defaultProfile,
        LlmRoutingContextProfile simplePackedProfile,
        int simpleMaxChars,
        int simpleMaxHistory)
    {
        // Extension point: если позже появится обучаемый scoring, здесь можно вернуть breakdown по весам feature-функций.
        var blocker = string.IsNullOrWhiteSpace(contextProfileBlockerReason)
            ? "none"
            : contextProfileBlockerReason;
        return string.Join(
            " ",
            $"mode={routerMode};",
            selectedReason,
            $"intent={intentClass};",
            $"contextProfile={contextProfile};",
            $"blocker={blocker};",
            $"profiles(default:chars={defaultProfile.EstimatedInputChars},history={defaultProfile.HistoryMessageCount};",
            $"simplePacked:chars={simplePackedProfile.EstimatedInputChars}/{simpleMaxChars},history={simplePackedProfile.HistoryMessageCount}/{simpleMaxHistory}).");
    }
}
