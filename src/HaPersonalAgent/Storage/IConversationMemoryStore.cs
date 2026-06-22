using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Storage;

/// <summary>
/// What: the conversation-scoped persistence surface used by the dialogue layer
/// (<see cref="DialogueService"/> and <see cref="BoundedChatHistoryProvider"/>) — recent messages,
/// the rolling summary and raw events.
/// Why: HPA-002 introduces this seam so the conversation-memory backend can be swapped (SQLite today,
/// Memory MCP later per HPA-004) without touching the dialogue layer. Pure seam: no behavior change.
/// How: <see cref="SqliteConversationMemoryStore"/> delegates 1:1 to <see cref="AgentStateRepository"/>;
/// consumers depend on this interface instead of the repository directly. Orthogonal concerns
/// (Telegram offset, confirmations) stay on the repository.
/// </summary>
public interface IConversationMemoryStore
{
    // Conversation messages (bounded recent window).
    Task<IReadOnlyList<AgentConversationMessage>> GetConversationMessagesAsync(
        string conversationKey,
        int limit,
        CancellationToken cancellationToken);

    Task<int> GetConversationMessageCountAsync(
        string conversationKey,
        CancellationToken cancellationToken);

    Task AppendConversationMessagesAsync(
        string conversationKey,
        IEnumerable<AgentConversationMessage> messages,
        CancellationToken cancellationToken);

    Task TrimConversationMessagesAsync(
        string conversationKey,
        int maxMessages,
        CancellationToken cancellationToken);

    Task ClearConversationMessagesAsync(
        string conversationKey,
        CancellationToken cancellationToken);

    Task<long?> GetLatestConversationMessageIdAsync(
        string conversationKey,
        CancellationToken cancellationToken);

    // Rolling summary memory.
    Task<ConversationSummaryMemory?> GetConversationSummaryAsync(
        string conversationKey,
        CancellationToken cancellationToken);

    Task UpsertConversationSummaryAsync(
        ConversationSummaryMemory summaryMemory,
        CancellationToken cancellationToken);

    Task ClearConversationSummaryAsync(
        string conversationKey,
        CancellationToken cancellationToken);

    // Raw events (dialogue audit log).
    Task AppendRawEventsAsync(
        IEnumerable<RawEventEntry> events,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RawEventRecord>> GetRawEventsAsync(
        string conversationKey,
        int limit,
        CancellationToken cancellationToken);

    Task<int> GetRawEventCountAsync(
        string conversationKey,
        CancellationToken cancellationToken);
}
