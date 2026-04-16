using HaPersonalAgent.Agent;
using HaPersonalAgent.Dialogue;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты policy обновления persisted summary.
/// Зачем: нужно гарантировать прозрачные reason-коды refresh и стабильную эвристику topic-shift для HAAG-055.
/// Как: проверяет сценарии missing/threshold/topic-shift/no-refresh/status.
/// </summary>
public sealed class PersistedSummaryRefreshPolicyTests
{
    [Fact]
    public void EvaluateAuto_returns_missing_when_summary_absent()
    {
        var decision = PersistedSummaryRefreshPolicy.EvaluateAuto(
            summary: null,
            messagesSinceSummary: 0,
            refreshMessageThreshold: 12,
            userText: "привет",
            history: Array.Empty<AgentConversationMessage>());

        Assert.True(decision.ShouldRefresh);
        Assert.Equal(PersistedSummaryRefreshReasons.Missing, decision.Reason);
    }

    [Fact]
    public void EvaluateAuto_returns_threshold_when_message_limit_reached()
    {
        var decision = PersistedSummaryRefreshPolicy.EvaluateAuto(
            summary: CreateSummary(),
            messagesSinceSummary: 12,
            refreshMessageThreshold: 12,
            userText: "что по проекту",
            history: CreateHistory("Стройка фундамента продолжается."));

        Assert.True(decision.ShouldRefresh);
        Assert.Equal(PersistedSummaryRefreshReasons.Threshold, decision.Reason);
    }

    [Fact]
    public void EvaluateAuto_returns_topic_shift_when_recent_user_topic_changes_hard()
    {
        var decision = PersistedSummaryRefreshPolicy.EvaluateAuto(
            summary: CreateSummary(),
            messagesSinceSummary: 8,
            refreshMessageThreshold: 12,
            userText: "составь график вакцинации щенка офелии по возрасту",
            history: CreateHistory(
            [
                "подскажи по стройке каркаса и выбору досок",
                "какое сечение досок взять для крыши",
                "как хранить доски перед монтажом",
            ]));

        Assert.True(decision.ShouldRefresh);
        Assert.Equal(PersistedSummaryRefreshReasons.TopicShift, decision.Reason);
    }

    [Fact]
    public void EvaluateAuto_returns_none_when_no_signal()
    {
        var decision = PersistedSummaryRefreshPolicy.EvaluateAuto(
            summary: CreateSummary(),
            messagesSinceSummary: 3,
            refreshMessageThreshold: 12,
            userText: "какой шаг дальше по стройке каркаса",
            history: CreateHistory("стройка каркаса и подбор досок"));

        Assert.False(decision.ShouldRefresh);
        Assert.Equal(PersistedSummaryRefreshReasons.None, decision.Reason);
    }

    [Fact]
    public void EvaluateStatus_reports_expected_reason()
    {
        var missing = PersistedSummaryRefreshPolicy.EvaluateStatus(
            summary: null,
            messagesSinceSummary: 0,
            refreshMessageThreshold: 12);
        var threshold = PersistedSummaryRefreshPolicy.EvaluateStatus(
            summary: CreateSummary(),
            messagesSinceSummary: 13,
            refreshMessageThreshold: 12);
        var none = PersistedSummaryRefreshPolicy.EvaluateStatus(
            summary: CreateSummary(),
            messagesSinceSummary: 4,
            refreshMessageThreshold: 12);

        Assert.Equal(PersistedSummaryRefreshReasons.Missing, missing.Reason);
        Assert.Equal(PersistedSummaryRefreshReasons.Threshold, threshold.Reason);
        Assert.Equal(PersistedSummaryRefreshReasons.None, none.Reason);
    }

    private static ConversationSummaryMemory CreateSummary() =>
        new(
            "telegram:1:1",
            "## Контекст пользователя\n- Стройка дома\n\n## Факты и решения\n- Каркас из сухой доски.",
            DateTimeOffset.UtcNow,
            SourceLastMessageId: 10,
            SummaryVersion: 1);

    private static IReadOnlyList<AgentConversationMessage> CreateHistory(string userText) =>
        CreateHistory([userText]);

    private static IReadOnlyList<AgentConversationMessage> CreateHistory(IEnumerable<string> userTexts) =>
    [
        .. userTexts.Select(text => new AgentConversationMessage(AgentConversationRole.User, text, DateTimeOffset.UtcNow)),
    ];
}
