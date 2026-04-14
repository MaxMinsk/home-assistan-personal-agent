namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: контракт executor для одного типа подтверждаемого действия.
/// Зачем: ConfirmationService должен уметь запускать Home Assistant MCP, file write/delete и будущие risky operations через единый механизм.
/// Как: executor объявляет ActionKind и выполняет PendingConfirmation только после approve.
/// </summary>
public interface IConfirmationActionExecutor
{
    string ActionKind { get; }

    Task<ConfirmationActionExecutionResult> ExecuteAsync(
        PendingConfirmation confirmation,
        CancellationToken cancellationToken);
}
