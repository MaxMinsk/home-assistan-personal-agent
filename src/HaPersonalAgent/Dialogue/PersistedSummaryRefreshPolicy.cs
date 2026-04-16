using HaPersonalAgent.Agent;
using System.Text.RegularExpressions;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: policy вычисления необходимости обновить persisted summary.
/// Зачем: HAAG-055 требует явный refresh reason (missing/threshold/topic-shift/manual), а не только boolean-флаг.
/// Как: применяет детерминированные правила по состоянию summary, количеству новых сообщений и эвристике смены темы.
/// </summary>
public static class PersistedSummaryRefreshPolicy
{
    private const int TopicShiftMinMessagesSinceSummary = 6;
    private const int TopicShiftMinQueryKeywords = 3;
    private const double TopicShiftOverlapThreshold = 0.15d;

    private static readonly Regex KeywordRegex = new(
        @"[\p{L}\p{N}_-]{4,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PersistedSummaryRefreshDecision EvaluateAuto(
        ConversationSummaryMemory? summary,
        int messagesSinceSummary,
        int refreshMessageThreshold,
        string userText,
        IReadOnlyList<AgentConversationMessage> history)
    {
        if (summary is null)
        {
            return PersistedSummaryRefreshDecision.Refresh(PersistedSummaryRefreshReasons.Missing);
        }

        if (messagesSinceSummary >= refreshMessageThreshold)
        {
            return PersistedSummaryRefreshDecision.Refresh(PersistedSummaryRefreshReasons.Threshold);
        }

        if (IsTopicShiftDetected(summary, messagesSinceSummary, userText, history))
        {
            return PersistedSummaryRefreshDecision.Refresh(PersistedSummaryRefreshReasons.TopicShift);
        }

        return PersistedSummaryRefreshDecision.NoRefresh();
    }

    public static PersistedSummaryRefreshDecision EvaluateStatus(
        ConversationSummaryMemory? summary,
        int messagesSinceSummary,
        int refreshMessageThreshold)
    {
        if (summary is null)
        {
            return PersistedSummaryRefreshDecision.Refresh(PersistedSummaryRefreshReasons.Missing);
        }

        if (messagesSinceSummary >= refreshMessageThreshold)
        {
            return PersistedSummaryRefreshDecision.Refresh(PersistedSummaryRefreshReasons.Threshold);
        }

        return PersistedSummaryRefreshDecision.NoRefresh();
    }

    private static bool IsTopicShiftDetected(
        ConversationSummaryMemory summary,
        int messagesSinceSummary,
        string userText,
        IReadOnlyList<AgentConversationMessage> history)
    {
        if (messagesSinceSummary < TopicShiftMinMessagesSinceSummary)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(summary.Summary) || string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }

        var requestKeywords = ExtractKeywords(userText);
        if (requestKeywords.Count < TopicShiftMinQueryKeywords)
        {
            return false;
        }

        var historyText = string.Join(
            " ",
            history
                .Where(message => message.Role == AgentConversationRole.User)
                .TakeLast(6)
                .Select(message => message.Text));
        if (string.IsNullOrWhiteSpace(historyText))
        {
            return false;
        }

        var historyKeywords = ExtractKeywords(historyText);
        if (historyKeywords.Count == 0)
        {
            return false;
        }

        var overlap = requestKeywords.Count(keyword => historyKeywords.Contains(keyword));
        var overlapRatio = overlap / (double)requestKeywords.Count;
        return overlapRatio <= TopicShiftOverlapThreshold;
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in KeywordRegex.Matches(text))
        {
            var value = match.Value.Trim();
            if (value.Length < 4)
            {
                continue;
            }

            keywords.Add(value);
        }

        return keywords;
    }
}

/// <summary>
/// Что: результат policy проверки refresh persisted summary.
/// Зачем: помогает передавать reason-код дальше в runtime/logs/status без расхождения boolean + строк.
/// Как: содержит bool-флаг необходимости refresh и нормализованную причину.
/// </summary>
public sealed record PersistedSummaryRefreshDecision(
    bool ShouldRefresh,
    string Reason)
{
    public static PersistedSummaryRefreshDecision Refresh(string reason) =>
        new(
            ShouldRefresh: true,
            Reason: PersistedSummaryRefreshReasons.Normalize(reason));

    public static PersistedSummaryRefreshDecision NoRefresh() =>
        new(
            ShouldRefresh: false,
            Reason: PersistedSummaryRefreshReasons.None);
}
