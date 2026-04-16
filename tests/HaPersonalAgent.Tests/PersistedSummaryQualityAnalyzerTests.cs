using HaPersonalAgent.Dialogue;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты анализа качества persisted summary.
/// Зачем: HAAG-055 требует мониторить структуру и полезность summary через числовые метрики.
/// Как: проверяет подсчет facts/conflicts и определение структурного контракта на типичных markdown-примерах.
/// </summary>
public sealed class PersistedSummaryQualityAnalyzerTests
{
    [Fact]
    public void Analyze_counts_meaningful_bullets_for_structured_summary()
    {
        const string summary =
            """
            ## Контекст пользователя
            - Пользователь развивает домашний AI-агент.

            ## Факты и решения
            - Выбран C# и MAF.
            - Telegram остается только transport-адаптером.

            ## Открытые задачи
            - Внедрить user-scoped memory.

            ## Ограничения и предпочтения
            - Деплой как Home Assistant add-on.

            ## Конфликты и обновления
            - Ранее планировался Azure-only путь, теперь выбран moonshot/openai-compatible backend.

            ## Source attribution
            - baseline_summary_used: yes
            - recent_tail_used: yes
            - source_notes: последние диалоги о памяти и релизах.
            """;

        var snapshot = PersistedSummaryQualityAnalyzer.Analyze(summary);

        Assert.True(snapshot.HasStructuredContract);
        Assert.Equal(2, snapshot.FactsCount);
        Assert.Equal(1, snapshot.OpenTasksCount);
        Assert.Equal(1, snapshot.ConstraintsCount);
        Assert.Equal(1, snapshot.ConflictsCount);
        Assert.Equal(9, snapshot.TotalMeaningfulBullets);
    }

    [Fact]
    public void Analyze_ignores_no_data_bullets()
    {
        const string summary =
            """
            ## Контекст пользователя
            - нет данных

            ## Факты и решения
            - нет данных

            ## Открытые задачи
            - нет данных

            ## Ограничения и предпочтения
            - нет данных

            ## Конфликты и обновления
            - нет данных

            ## Source attribution
            - baseline_summary_used: no
            - recent_tail_used: yes
            - source_notes: нет данных
            """;

        var snapshot = PersistedSummaryQualityAnalyzer.Analyze(summary);

        Assert.True(snapshot.HasStructuredContract);
        Assert.Equal(0, snapshot.FactsCount);
        Assert.Equal(0, snapshot.ConflictsCount);
        Assert.Equal(3, snapshot.TotalMeaningfulBullets);
    }

    [Fact]
    public void Analyze_returns_unstructured_for_freeform_text()
    {
        var snapshot = PersistedSummaryQualityAnalyzer.Analyze("Просто одно предложение без секций.");

        Assert.False(snapshot.HasStructuredContract);
        Assert.Equal(0, snapshot.FactsCount);
        Assert.Equal(0, snapshot.ConflictsCount);
    }
}
