namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: результат обработки пользовательской команды approve/reject.
/// Зачем: Telegram, Web UI и будущие adapters должны одинаково понимать, выполнено действие, отклонено, устарело или не найдено.
/// Как: ConfirmationService возвращает outcome вместе с готовым текстом для transport adapter.
/// </summary>
public enum ConfirmationDecisionOutcome
{
    Completed,
    Rejected,
    NotFound,
    Expired,
    AlreadyHandled,
    ExecutionFailed,
    ExecutorMissing,
}
