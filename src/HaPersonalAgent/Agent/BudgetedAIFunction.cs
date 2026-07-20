using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: обёртка над любым инструментом, которая считает вызовы и глушит их после исчерпания бюджета.
/// Зачем: единственный способ получить ЧЕСТНЫЙ счётчик tool-call'ов и жёсткий потолок, покрывающий и наши функции, и инструменты MCP.
/// Как: DelegatingAIFunction — схема и описание остаются от исходного инструмента, меняется только поведение вызова.
/// После исчерпания бюджета возвращается текстовый ответ-инструкция «заверши с тем, что есть», а не исключение:
/// модель должна корректно завершить ход, а не упасть.
/// </summary>
public sealed class BudgetedAIFunction : DelegatingAIFunction
{
    private readonly AgentRunBudget _budget;
    private readonly ILogger? _logger;

    public BudgetedAIFunction(AIFunction innerFunction, AgentRunBudget budget, ILogger? logger = null)
        : base(innerFunction)
    {
        _budget = budget;
        _logger = logger;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!_budget.TryConsumeToolCall())
        {
            _logger?.LogWarning(
                "Tool '{ToolName}' was blocked: the run exceeded its budget of {MaxToolCalls} tool calls.",
                Name,
                _budget.MaxToolCalls);

            return $"Tool budget exhausted: this run is limited to {_budget.MaxToolCalls} tool calls. "
                + "Do not call any more tools. Finish now and answer with what you already gathered, "
                + "and say plainly which parts you could not verify.";
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken);
    }
}
