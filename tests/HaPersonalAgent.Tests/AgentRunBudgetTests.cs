using HaPersonalAgent.Agent;
using Microsoft.Extensions.AI;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты бюджета вызовов инструментов в одном run (HPA-036).
/// Зачем: фоновый агент с веб-поиском может уйти в длинный дорогой цикл, а прервать его некому;
/// и до этого счётчик tool-call'ов в журнале был нулём, то есть вводил в заблуждение.
/// Как: проверяем сам счётчик и обёртку BudgetedAIFunction на настоящем AIFunction, без сети и LLM.
/// </summary>
public class AgentRunBudgetTests
{
    [Fact]
    public void Budget_allows_exactly_its_limit_and_then_refuses()
    {
        var budget = new AgentRunBudget(maxToolCalls: 3);

        Assert.True(budget.TryConsumeToolCall());
        Assert.True(budget.TryConsumeToolCall());
        Assert.True(budget.TryConsumeToolCall());
        Assert.False(budget.TryConsumeToolCall());

        Assert.True(budget.IsExhausted);
        Assert.Equal(3, budget.MaxToolCalls);
    }

    [Fact]
    public void Budget_is_never_created_with_a_useless_limit()
    {
        Assert.Equal(1, new AgentRunBudget(0).MaxToolCalls);
        Assert.Equal(1, new AgentRunBudget(-5).MaxToolCalls);
    }

    [Fact]
    public async Task Wrapped_tool_runs_until_the_budget_is_spent_then_tells_the_model_to_conclude()
    {
        var invocations = 0;
        var inner = AIFunctionFactory.Create(
            () =>
            {
                invocations++;
                return "real tool result";
            },
            name: "probe",
            description: "test tool");

        var budget = new AgentRunBudget(maxToolCalls: 2);
        var budgeted = new BudgetedAIFunction(inner, budget);

        // AIFunctionFactory отдаёт результат уже сериализованным (JsonElement), а обёртка его не трогает —
        // поэтому сравниваем по тексту, а не по типу.
        Assert.Equal("real tool result", (await budgeted.InvokeAsync(new AIFunctionArguments()))?.ToString());
        Assert.Equal("real tool result", (await budgeted.InvokeAsync(new AIFunctionArguments()))?.ToString());

        var blocked = await budgeted.InvokeAsync(new AIFunctionArguments());

        // Третий вызов не доходит до инструмента и возвращает инструкцию завершить ход,
        // а не исключение — модель должна корректно закончить, а не упасть.
        Assert.Equal(2, invocations);
        Assert.Contains("Tool budget exhausted", blocked?.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, budget.MaxToolCalls);
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void Wrapping_preserves_the_tool_name_so_the_model_sees_the_same_catalog()
    {
        var inner = AIFunctionFactory.Create(() => "x", name: "web_search", description: "d");

        var budgeted = new BudgetedAIFunction(inner, new AgentRunBudget(5));

        Assert.Equal("web_search", budgeted.Name);
    }
}
