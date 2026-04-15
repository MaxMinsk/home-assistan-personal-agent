namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: состояние пайплайна извлечения project capsules для одного conversation scope.
/// Зачем: extraction должен быть batched/идемпотентным и не повторно обрабатывать один и тот же диапазон raw_events.
/// Как: фиксирует id последнего обработанного raw event, момент последнего extraction и число запусков.
/// </summary>
public sealed record ProjectCapsuleExtractionState(
    string ConversationKey,
    long LastRawEventId,
    DateTimeOffset UpdatedAtUtc,
    int RunsCount);
