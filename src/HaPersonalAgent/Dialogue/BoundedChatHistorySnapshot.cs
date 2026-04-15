using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: результат загрузки bounded chat history для одного user turn.
/// Зачем: DialogueService должен передать в runtime два слоя памяти: recent turns из SQL и retrieval-контекст из vector overflow.
/// Как: recent сообщения идут в AgentContext.ConversationMessages, а RetrievedMemoryContext добавляется как отдельный memory блок в prompt.
/// </summary>
public sealed record BoundedChatHistorySnapshot(
    IReadOnlyList<AgentConversationMessage> RecentMessages,
    string? RetrievedMemoryContext,
    int RetrievedMemoryCount);
