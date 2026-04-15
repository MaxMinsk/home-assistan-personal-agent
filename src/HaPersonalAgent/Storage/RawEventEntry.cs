namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: входная append-модель сырого события для Raw Event Store.
/// Зачем: сервисы диалога/уведомлений должны писать immutable события как source of truth, не смешивая их с `conversation_messages`.
/// Как: хранит transport scope, тип события, текст payload и опциональные correlation/source поля; запись всегда добавляется новой строкой в `raw_events`.
/// </summary>
public sealed record RawEventEntry(
    string ConversationKey,
    string Transport,
    string ConversationId,
    string ParticipantId,
    string EventKind,
    string Payload,
    string? SourceId,
    string? CorrelationId,
    DateTimeOffset CreatedAtUtc)
{
    public static RawEventEntry Create(
        string conversationKey,
        string transport,
        string conversationId,
        string participantId,
        string eventKind,
        string payload,
        string? sourceId = null,
        string? correlationId = null,
        DateTimeOffset? createdAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new RawEventEntry(
            conversationKey.Trim(),
            transport.Trim(),
            conversationId.Trim(),
            participantId.Trim(),
            eventKind.Trim(),
            payload,
            string.IsNullOrWhiteSpace(sourceId) ? null : sourceId.Trim(),
            string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim(),
            createdAtUtc ?? DateTimeOffset.UtcNow);
    }
}
