namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: прочитанная из SQLite запись raw event.
/// Зачем: диагностика и будущие memory-пайплайны должны читать точный append-only журнал событий, включая system notifications и reset операции.
/// Как: повторяет структуру таблицы `raw_events`, включая автоинкрементный id для детерминированного порядка событий.
/// </summary>
public sealed record RawEventRecord(
    long Id,
    string ConversationKey,
    string Transport,
    string ConversationId,
    string ParticipantId,
    string EventKind,
    string Payload,
    string? SourceId,
    string? CorrelationId,
    DateTimeOffset CreatedAtUtc);
