namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: результат создания pending confirmation.
/// Зачем: агент должен получить готовый безопасный текст с id и командами approve/reject, но без фактического выполнения.
/// Как: ConfirmationService возвращает машинно-читаемые поля и user-facing message для ответа в transport adapter.
/// </summary>
public sealed record ConfirmationProposalResult(
    bool IsCreated,
    string Message,
    string? ConfirmationId,
    string? ApproveCommand,
    string? RejectCommand,
    DateTimeOffset? ExpiresAtUtc)
{
    public static ConfirmationProposalResult Rejected(string message) =>
        new(
            IsCreated: false,
            message,
            ConfirmationId: null,
            ApproveCommand: null,
            RejectCommand: null,
            ExpiresAtUtc: null);
}
