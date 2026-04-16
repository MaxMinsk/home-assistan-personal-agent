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
        var estimatedInputChars = EstimateInputChars(context, normalizedMessage);
        var historyMessageCount = context.ConversationMessages.Count;
        var maxCharsForSmall = Math.Clamp(options.RouterMaxInputCharsForSmall, 200, 24_000);
        var maxHistoryForSmall = Math.Clamp(options.RouterMaxHistoryMessagesForSmall, 2, 64);
        var deepKeywords = ParseKeywords(options.RouterDeepKeywords);

        var hasDeepIntent = HasDeepIntent(profile, normalizedMessage, deepKeywords);
        var fitsSmallContext = estimatedInputChars <= maxCharsForSmall
            && historyMessageCount <= maxHistoryForSmall;
        var isSmallEligibleProfile = profile is LlmExecutionProfile.ToolEnabled or LlmExecutionProfile.PureChat;
        var isSimplePrompt = IsSimpleOperationalPrompt(normalizedMessage);

        // Extension point: на следующем этапе сюда можно добавить feature flags:
        // 1) provider-specific budgets (tokens/$ per model),
        // 2) lightweight lexical classifier per domain,
        // 3) per-user calibration из исторической telemetry.
        var candidateDecision = SelectCandidateDecision(
            normalizedDefaultModel,
            normalizedSmallModel,
            hasDeepIntent,
            fitsSmallContext,
            isSmallEligibleProfile,
            isSimplePrompt);

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
            isSimplePrompt,
            fitsSmallContext,
            hasDeepIntent,
            estimatedInputChars,
            historyMessageCount,
            maxCharsForSmall,
            maxHistoryForSmall);

        return candidateDecision with
        {
            RouterMode = routerMode,
            IsApplied = isApplied,
            SelectedModel = selectedModel,
            ThinkingModeOverride = thinkingModeOverride,
            Reason = reason,
            EstimatedInputChars = estimatedInputChars,
            HistoryMessageCount = historyMessageCount,
        };
    }

    private static LlmRoutingDecision SelectCandidateDecision(
        string defaultModel,
        string smallModel,
        bool hasDeepIntent,
        bool fitsSmallContext,
        bool isSmallEligibleProfile,
        bool isSimplePrompt)
    {
        if (hasDeepIntent)
        {
            return new LlmRoutingDecision(
                RouterMode: LlmRouterModes.Off,
                IsApplied: false,
                ModelTarget: LlmRoutingDecision.ModelTargetDefault,
                SelectedModel: defaultModel,
                ReasoningTarget: LlmRoutingDecision.ReasoningTargetDeep,
                ThinkingModeOverride: LlmThinkingModes.Enabled,
                DecisionBucket: LlmRoutingDecision.DecisionBucketDefaultDeep,
                Reason: "deep intent marker detected in request/profile.",
                EstimatedInputChars: 0,
                HistoryMessageCount: 0);
        }

        if (isSmallEligibleProfile && fitsSmallContext && isSimplePrompt)
        {
            return new LlmRoutingDecision(
                RouterMode: LlmRouterModes.Off,
                IsApplied: false,
                ModelTarget: LlmRoutingDecision.ModelTargetSmallContextFast,
                SelectedModel: smallModel,
                ReasoningTarget: LlmRoutingDecision.ReasoningTargetDisabled,
                ThinkingModeOverride: LlmThinkingModes.Disabled,
                DecisionBucket: LlmRoutingDecision.DecisionBucketSmallDisabled,
                Reason: "short operational prompt in small context budget.",
                EstimatedInputChars: 0,
                HistoryMessageCount: 0);
        }

        return new LlmRoutingDecision(
            RouterMode: LlmRouterModes.Off,
            IsApplied: false,
            ModelTarget: LlmRoutingDecision.ModelTargetDefault,
            SelectedModel: defaultModel,
            ReasoningTarget: LlmRoutingDecision.ReasoningTargetProviderDefault,
            ThinkingModeOverride: null,
            DecisionBucket: LlmRoutingDecision.DecisionBucketDefaultProviderDefault,
            Reason: "default path keeps provider model and reasoning mode.",
            EstimatedInputChars: 0,
            HistoryMessageCount: 0);
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

    private static bool IsSimpleOperationalPrompt(string message)
    {
        if (message.Length > 260)
        {
            return false;
        }

        if (message.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        var tokenCount = message.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
        if (tokenCount > 48)
        {
            return false;
        }

        foreach (var marker in ComplexIntentMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static int EstimateInputChars(AgentContext context, string message)
    {
        var total = message.Length;
        foreach (var historyMessage in context.ConversationMessages)
        {
            total += historyMessage.Text?.Length ?? 0;
        }

        total += context.PersistedSummary?.Length ?? 0;
        total += context.RetrievedMemoryContext?.Length ?? 0;
        return total;
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
        bool isSimplePrompt,
        bool fitsSmallContext,
        bool hasDeepIntent,
        int estimatedInputChars,
        int historyMessageCount,
        int maxCharsForSmall,
        int maxHistoryForSmall)
    {
        // Extension point: если позже появится обучаемый scoring, здесь можно вернуть breakdown по весам feature-функций.
        return string.Join(
            " ",
            $"mode={routerMode};",
            selectedReason,
            $"features(simple={isSimplePrompt}, smallContext={fitsSmallContext}, deepIntent={hasDeepIntent}, inputChars={estimatedInputChars}/{maxCharsForSmall}, historyMessages={historyMessageCount}/{maxHistoryForSmall}).");
    }
}
