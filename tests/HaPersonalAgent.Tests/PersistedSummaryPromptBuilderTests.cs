using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты builder-а prompt для persisted summary.
/// Зачем: HAAG-055 требует детерминированный merge-контракт и обязательную структуру summary prompt.
/// Как: проверяет наличие ключевых правил, секций и baseline-блока в сформированном prompt.
/// </summary>
public sealed class PersistedSummaryPromptBuilderTests
{
    [Fact]
    public void Build_contains_required_structure_and_merge_rules()
    {
        var builder = new PersistedSummaryPromptBuilder();

        var prompt = builder.Build(
            persistedSummary: null,
            refreshReason: PersistedSummaryRefreshReasons.Threshold,
            messagesSincePersistedSummary: 14);

        Assert.Contains("new_summary = merge(old_summary, summary(new_tail))", prompt, StringComparison.Ordinal);
        Assert.Contains("## Контекст пользователя", prompt, StringComparison.Ordinal);
        Assert.Contains("## Факты и решения", prompt, StringComparison.Ordinal);
        Assert.Contains("## Конфликты и обновления", prompt, StringComparison.Ordinal);
        Assert.Contains("## Source attribution", prompt, StringComparison.Ordinal);
        Assert.Contains("refresh_reason: threshold", prompt, StringComparison.Ordinal);
        Assert.Contains("messages_since_previous_summary: 14", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_includes_baseline_block_when_summary_exists()
    {
        var builder = new PersistedSummaryPromptBuilder();
        const string baseline = "Анна — дочь пользователя, 7 лет.";

        var prompt = builder.Build(
            baseline,
            refreshReason: PersistedSummaryRefreshReasons.Manual,
            messagesSincePersistedSummary: 0);

        Assert.Contains("Existing persisted summary baseline:", prompt, StringComparison.Ordinal);
        Assert.Contains(baseline, prompt, StringComparison.Ordinal);
        Assert.Contains("Do not drop baseline facts", prompt, StringComparison.Ordinal);
        Assert.Contains("baseline_summary_present: yes", prompt, StringComparison.Ordinal);
    }
}
