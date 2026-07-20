using HaPersonalAgent.Autonomous;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты форматирования и нарезки брифа автономного агента (HPA-032).
/// Зачем: длинная сводка не должна молча обрезаться при доставке в Telegram, а вопросы должны быть видимы и пронумерованы.
/// Как: чистые функции форматтера проверяются без сети и без Telegram.
/// </summary>
public class AutonomousAgentBriefFormatterTests
{
    [Fact]
    public void Brief_contains_name_summary_numbered_questions_and_reply_hint()
    {
        var definition = AutonomousAgentDefinition.Create(
            "Бизнес в Минске",
            "миссия",
            AutonomousAgentScheduleKind.Weekly);
        var output = new AutonomousRunOutput(
            "Нашёл три ниши.",
            new[] { "Интересует B2B?", "Бюджет до 20k?" },
            Array.Empty<string>(),
            "дальше");

        var brief = AutonomousAgentBriefFormatter.BuildBrief(definition, output);

        Assert.Contains("Бизнес в Минске", brief, StringComparison.Ordinal);
        Assert.Contains("Нашёл три ниши.", brief, StringComparison.Ordinal);
        Assert.Contains("1. Интересует B2B?", brief, StringComparison.Ordinal);
        Assert.Contains("2. Бюджет до 20k?", brief, StringComparison.Ordinal);
        Assert.Contains("реплаем", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_brief_is_delivered_as_a_single_message()
    {
        var chunks = AutonomousAgentBriefFormatter.Chunk("короткая сводка");

        Assert.Single(chunks);
        Assert.Equal("короткая сводка", chunks[0]);
    }

    [Fact]
    public void Long_brief_is_split_without_losing_content()
    {
        // 60 абзацев по ~100 символов — заведомо больше лимита Telegram.
        var paragraphs = Enumerable.Range(1, 60)
            .Select(index => $"Пункт {index}: " + new string('я', 100))
            .ToArray();
        var text = string.Join("\n\n", paragraphs);

        var chunks = AutonomousAgentBriefFormatter.Chunk(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
            Assert.True(chunk.Length <= AutonomousAgentBriefFormatter.MaxTelegramMessageLength));

        // Ни один пункт не должен потеряться при нарезке.
        var rejoined = string.Concat(chunks.Select(chunk => chunk.Replace("\n", string.Empty, StringComparison.Ordinal)));
        foreach (var paragraph in paragraphs)
        {
            var expected = paragraph.Replace("\n", string.Empty, StringComparison.Ordinal);
            Assert.Contains(expected, rejoined, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Chunking_falls_back_to_a_hard_split_when_there_is_no_whitespace()
    {
        var text = new string('x', 9000);

        var chunks = AutonomousAgentBriefFormatter.Chunk(text, maxLength: 1000);

        Assert.Equal(9, chunks.Count);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 1000));
        Assert.Equal(9000, chunks.Sum(chunk => chunk.Length));
    }

    [Fact]
    public void Empty_summary_still_produces_a_deliverable_message()
    {
        var chunks = AutonomousAgentBriefFormatter.Chunk(string.Empty);

        Assert.Single(chunks);
        Assert.False(string.IsNullOrWhiteSpace(chunks[0]));
    }
}
