namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: универсальное действие, которое агент предложил выполнить после пользовательского подтверждения.
/// Зачем: confirmation policy должна защищать не только Home Assistant, но и будущие операции с файлами, shell/workflows и другие risky actions.
/// Как: хранит action kind, operation name, JSON payload, scope диалога, срок жизни, статус и результат выполнения.
/// </summary>
public sealed record PendingConfirmation(
    string Id,
    string ActionKind,
    string ConversationKey,
    string ParticipantId,
    string OperationName,
    string PayloadJson,
    string Summary,
    string Risk,
    ConfirmationActionStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string CorrelationId,
    string? ResultJson,
    string? Error)
{
    public bool IsExpired(DateTimeOffset utcNow) => utcNow >= ExpiresAtUtc;
}
