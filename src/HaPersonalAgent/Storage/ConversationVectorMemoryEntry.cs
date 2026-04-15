using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: запись для upsert в таблицу conversation_vector_memory.
/// Зачем: overflow turns из bounded chat window нужно переносить в долговременную vector memory без потери source id.
/// Как: хранит source_message_id, роль, текст и сериализованный embedding; уникальность обеспечивается парой (conversation_key, source_message_id).
/// </summary>
public sealed record ConversationVectorMemoryEntry(
    string ConversationKey,
    long SourceMessageId,
    AgentConversationRole Role,
    string Content,
    string Embedding,
    DateTimeOffset CreatedAtUtc);
