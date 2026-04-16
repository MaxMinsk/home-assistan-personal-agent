using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: builder context profiles для adaptive routing.
/// Зачем: HAAG-056 требует отделить intent classification от context packing, чтобы simple-route не блокировался сырым большим контекстом.
/// Как: строит два профиля — `default_full` и `simple_packed` — и возвращает упакованный AgentContext плюс budget-диагностику.
/// </summary>
public sealed class LlmRoutingContextProfileBuilder
{
    private const int MinSimpleSummaryChars = 320;
    private const int MaxSimpleSummaryChars = 2_400;

    public LlmRoutingContextProfile BuildDefault(
        AgentContext context,
        string userMessage)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        return new LlmRoutingContextProfile(
            LlmRoutingDecision.ContextProfileDefaultFull,
            context,
            EstimateInputChars(context, userMessage),
            context.ConversationMessages.Count,
            context.PersistedSummary?.Length ?? 0,
            context.RetrievedMemoryContext?.Length ?? 0);
    }

    public LlmRoutingContextProfile BuildSimplePacked(
        AgentContext context,
        string userMessage,
        LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(options);

        var simpleMaxInputChars = Math.Clamp(options.RouterSimpleMaxInputChars, 400, 24_000);
        var simpleMaxHistoryMessages = Math.Clamp(options.RouterSimpleMaxHistoryMessages, 2, 64);
        var summaryBudget = Math.Clamp(simpleMaxInputChars / 2, MinSimpleSummaryChars, MaxSimpleSummaryChars);
        var packedHistory = context.ConversationMessages
            .TakeLast(simpleMaxHistoryMessages)
            .ToArray();
        var packedSummary = Truncate(context.PersistedSummary, summaryBudget);

        var packedExecutionProfile = options.RouterSimpleAllowTools
            ? context.ExecutionProfile
            : context.ExecutionProfile == LlmExecutionProfile.ToolEnabled
                ? LlmExecutionProfile.PureChat
                : context.ExecutionProfile;

        var packedContext = context with
        {
            ConversationMessages = packedHistory,
            PersistedSummary = packedSummary,
            RetrievedMemoryContext = null,
            RetrievedMemoryCount = 0,
            ExecutionProfile = packedExecutionProfile,
        };

        return new LlmRoutingContextProfile(
            LlmRoutingDecision.ContextProfileSimplePacked,
            packedContext,
            EstimateInputChars(packedContext, userMessage),
            packedHistory.Length,
            packedSummary?.Length ?? 0,
            RetrievedMemoryChars: 0);
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

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }
}

/// <summary>
/// Что: результат построения routing context profile.
/// Зачем: resolver/runtime должны видеть какой именно профиль использовался и сколько budget занял этот профиль.
/// Как: содержит имя профиля, итоговый AgentContext и счетчики по input/history/summary/retrieved memory.
/// </summary>
public sealed record LlmRoutingContextProfile(
    string Profile,
    AgentContext Context,
    int EstimatedInputChars,
    int HistoryMessageCount,
    int SummaryChars,
    int RetrievedMemoryChars);
