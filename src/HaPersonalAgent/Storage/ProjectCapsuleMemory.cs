namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: сохраненная проектная капсула памяти по одному conversation scope.
/// Зачем: agent должен хранить не только turns/summary, но и устойчивые карточки проектов (стройка, собака, дом и т.д.) с source attribution.
/// Как: хранит ключ капсулы, заголовок, структурированный markdown-контент, confidence, source_event_id и version для идемпотентных апдейтов.
/// </summary>
public sealed record ProjectCapsuleMemory(
    string ConversationKey,
    string CapsuleKey,
    string Title,
    string ContentMarkdown,
    string Scope,
    double Confidence,
    long SourceEventId,
    DateTimeOffset UpdatedAtUtc,
    int Version);
