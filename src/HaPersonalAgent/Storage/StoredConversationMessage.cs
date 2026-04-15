using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Storage;

/// <summary>
/// Что: сообщение из таблицы conversation_messages вместе с автоинкрементным id.
/// Зачем: bounded history provider должен архивировать overflow-сообщения в vector memory и для этого нужен стабильный source_message_id.
/// Как: repository возвращает эту модель только для внутренних memory-пайплайнов; transport слой продолжает работать с AgentConversationMessage.
/// </summary>
public sealed record StoredConversationMessage(
    long Id,
    AgentConversationRole Role,
    string Text,
    DateTimeOffset CreatedAtUtc);
