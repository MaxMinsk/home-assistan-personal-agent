namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: append-only audit event для confirmation action.
/// Зачем: risky actions должны оставлять короткий след без смешивания с обычной памятью диалога.
/// Как: repository пишет событие в отдельную таблицу confirmation_audit с action kind, operation и sanitized details.
/// </summary>
public sealed record ConfirmationAuditRecord(
    string ConfirmationId,
    string ActionKind,
    string ConversationKey,
    string ParticipantId,
    string OperationName,
    string Event,
    string Summary,
    string? Details,
    DateTimeOffset CreatedAtUtc);
