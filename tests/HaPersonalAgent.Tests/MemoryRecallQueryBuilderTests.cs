using HaPersonalAgent.Memory;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты построения recall-запроса для Memory MCP.
/// Зачем: notes_search матчит токены по AND, поэтому сырая фраза-вопрос возвращала ноль; билдер
/// должен убирать стоп-слова/пунктуацию и префиксовать содержательные токены.
/// Как: проверяет очистку, префиксацию и fallback на исходный текст.
/// </summary>
public class MemoryRecallQueryBuilderTests
{
    [Fact]
    public void Build_strips_stopwords_and_prefixes_content_tokens()
    {
        Assert.Equal("перцев*", MemoryRecallQueryBuilder.Build("сколько у меня перцев?"));
    }

    [Fact]
    public void Build_prefixes_each_english_content_token()
    {
        Assert.Equal("pepper* seed* varieties*", MemoryRecallQueryBuilder.Build("pepper seed varieties"));
    }

    [Fact]
    public void Build_falls_back_to_original_when_only_stopwords_remain()
    {
        Assert.Equal("у меня что", MemoryRecallQueryBuilder.Build("у меня что"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Build_returns_empty_for_blank_input(string? input)
    {
        Assert.Equal(string.Empty, MemoryRecallQueryBuilder.Build(input));
    }
}
