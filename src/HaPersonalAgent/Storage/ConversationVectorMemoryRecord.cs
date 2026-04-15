using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: прочитанная запись из conversation_vector_memory.
/// Зачем: retrieval шаг должен вычислять similarity между текущим запросом и архивными overflow turns.
/// Как: содержит id строки, source_message_id исходного turn, роль, текст и embedding в сериализованном виде.
/// </summary>
public sealed record ConversationVectorMemoryRecord(
    long Id,
    string ConversationKey,
    long SourceMessageId,
    AgentConversationRole Role,
    string Content,
    string Embedding,
    DateTimeOffset CreatedAtUtc);
