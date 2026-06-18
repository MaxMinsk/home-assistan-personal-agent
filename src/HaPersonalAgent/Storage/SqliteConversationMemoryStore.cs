using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Storage;

/// <summary>
/// What: SQLite-backed <see cref="IConversationMemoryStore"/> implementation.
/// Why: preserves today's behavior exactly for HPA-002 — it is a thin seam over the existing
/// <see cref="AgentStateRepository"/>, so moving the dialogue layer onto the interface changes nothing.
/// How: every member delegates 1:1 to the repository. A future Memory MCP-backed store (HPA-004)
/// implements the same interface without touching consumers.
/// </summary>
public sealed class SqliteConversationMemoryStore : IConversationMemoryStore
{
    private readonly AgentStateRepository _repository;

    public SqliteConversationMemoryStore(AgentStateRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        _repository = repository;
    }

    public Task<IReadOnlyList<AgentConversationMessage>> GetConversationMessagesAsync(
        string conversationKey,
        int limit,
        CancellationToken cancellationToken) =>
        _repository.GetConversationMessagesAsync(conversationKey, limit, cancellationToken);

    public Task<int> GetConversationMessageCountAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.GetConversationMessageCountAsync(conversationKey, cancellationToken);

    public Task AppendConversationMessagesAsync(
        string conversationKey,
        IEnumerable<AgentConversationMessage> messages,
        CancellationToken cancellationToken) =>
        _repository.AppendConversationMessagesAsync(conversationKey, messages, cancellationToken);

    public Task TrimConversationMessagesAsync(
        string conversationKey,
        int maxMessages,
        CancellationToken cancellationToken) =>
        _repository.TrimConversationMessagesAsync(conversationKey, maxMessages, cancellationToken);

    public Task ClearConversationMessagesAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.ClearConversationMessagesAsync(conversationKey, cancellationToken);

    public Task<long?> GetLatestConversationMessageIdAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.GetLatestConversationMessageIdAsync(conversationKey, cancellationToken);

    public Task<ConversationSummaryMemory?> GetConversationSummaryAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.GetConversationSummaryAsync(conversationKey, cancellationToken);

    public Task UpsertConversationSummaryAsync(
        ConversationSummaryMemory summaryMemory,
        CancellationToken cancellationToken) =>
        _repository.UpsertConversationSummaryAsync(summaryMemory, cancellationToken);

    public Task ClearConversationSummaryAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.ClearConversationSummaryAsync(conversationKey, cancellationToken);

    public Task AppendRawEventsAsync(
        IEnumerable<RawEventEntry> events,
        CancellationToken cancellationToken) =>
        _repository.AppendRawEventsAsync(events, cancellationToken);

    public Task<IReadOnlyList<RawEventRecord>> GetRawEventsAsync(
        string conversationKey,
        int limit,
        CancellationToken cancellationToken) =>
        _repository.GetRawEventsAsync(conversationKey, limit, cancellationToken);

    public Task<int> GetRawEventCountAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.GetRawEventCountAsync(conversationKey, cancellationToken);

    public Task<IReadOnlyList<ProjectCapsuleMemory>> GetProjectCapsulesAsync(
        string conversationKey,
        int limit,
        CancellationToken cancellationToken) =>
        _repository.GetProjectCapsulesAsync(conversationKey, limit, cancellationToken);

    public Task<int> GetProjectCapsuleCountAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.GetProjectCapsuleCountAsync(conversationKey, cancellationToken);

    public Task<long?> GetProjectCapsuleLatestSourceEventIdAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.GetProjectCapsuleLatestSourceEventIdAsync(conversationKey, cancellationToken);

    public Task<DateTimeOffset?> GetProjectCapsuleLastUpdatedAtUtcAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.GetProjectCapsuleLastUpdatedAtUtcAsync(conversationKey, cancellationToken);

    public Task<ProjectCapsuleExtractionState?> GetProjectCapsuleExtractionStateAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.GetProjectCapsuleExtractionStateAsync(conversationKey, cancellationToken);

    public Task ClearProjectCapsulesAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.ClearProjectCapsulesAsync(conversationKey, cancellationToken);

    public Task ClearProjectCapsuleExtractionStateAsync(
        string conversationKey,
        CancellationToken cancellationToken) =>
        _repository.ClearProjectCapsuleExtractionStateAsync(conversationKey, cancellationToken);
}
