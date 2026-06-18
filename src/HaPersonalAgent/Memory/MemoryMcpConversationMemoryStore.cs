using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Memory;

/// <summary>
/// What: an <see cref="IConversationMemoryStore"/> decorator that mirrors the durable conversation summary
/// to Memory MCP (HPA-004).
/// Why: when memory_store_type=memory_mcp, the rolling summary is written to Memory MCP as a lossless
/// `conversation_summary` note (domain home) so it is durable and visible there — while the short-term
/// window stays local and the hot path is never blocked on remote calls.
/// How: wraps the SQLite store (the local source of truth for reads/latency); on summary upsert it also
/// best-effort mirrors to Memory MCP (short timeout, never throws). All other operations delegate to inner.
/// Capsule forward-writes (HPA-011) and backfill (HPA-010) are tracked separately.
/// </summary>
public sealed class MemoryMcpConversationMemoryStore : IConversationMemoryStore
{
    private static readonly TimeSpan MirrorTimeout = TimeSpan.FromSeconds(5);

    private readonly IConversationMemoryStore _inner;
    private readonly IMemoryMcpClient _memoryClient;
    private readonly IOptions<MemoryMcpOptions> _options;
    private readonly ILogger<MemoryMcpConversationMemoryStore> _logger;

    public MemoryMcpConversationMemoryStore(
        IConversationMemoryStore inner,
        IMemoryMcpClient memoryClient,
        IOptions<MemoryMcpOptions> options,
        ILogger<MemoryMcpConversationMemoryStore> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(memoryClient);
        ArgumentNullException.ThrowIfNull(options);

        _inner = inner;
        _memoryClient = memoryClient;
        _options = options;
        _logger = logger;
    }

    // --- Durable: conversation summary (write-through mirror to Memory MCP) ---

    // Reads stay local so the hot path is never blocked on a remote call; MCP holds the lossless mirror.
    public Task<ConversationSummaryMemory?> GetConversationSummaryAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.GetConversationSummaryAsync(conversationKey, cancellationToken);

    public async Task UpsertConversationSummaryAsync(ConversationSummaryMemory summaryMemory, CancellationToken cancellationToken)
    {
        await _inner.UpsertConversationSummaryAsync(summaryMemory, cancellationToken);
        await MirrorSummaryAsync(summaryMemory, cancellationToken);
    }

    public Task ClearConversationSummaryAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.ClearConversationSummaryAsync(conversationKey, cancellationToken);

    private async Task MirrorSummaryAsync(ConversationSummaryMemory summary, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MirrorTimeout);

            var arguments = MemoryMcpSummaryMapping.BuildUpsertArguments(summary, ApplicationInfo.Name);
            var result = await _memoryClient.CallToolAsync("notes_upsert", arguments, timeout.Token);
            if (result.IsError)
            {
                _logger.LogWarning(
                    "Memory MCP rejected the conversation summary mirror for {ConversationKey}: {Detail}",
                    summary.ConversationKey,
                    result.Text);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Memory MCP conversation summary mirror failed for {ConversationKey}; continuing on local memory.",
                summary.ConversationKey);
        }
    }

    // --- Short-term window, raw events, capsule reads, extraction state: stay local ---

    public Task<IReadOnlyList<AgentConversationMessage>> GetConversationMessagesAsync(string conversationKey, int limit, CancellationToken cancellationToken) =>
        _inner.GetConversationMessagesAsync(conversationKey, limit, cancellationToken);

    public Task<int> GetConversationMessageCountAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.GetConversationMessageCountAsync(conversationKey, cancellationToken);

    public Task AppendConversationMessagesAsync(string conversationKey, IEnumerable<AgentConversationMessage> messages, CancellationToken cancellationToken) =>
        _inner.AppendConversationMessagesAsync(conversationKey, messages, cancellationToken);

    public Task TrimConversationMessagesAsync(string conversationKey, int maxMessages, CancellationToken cancellationToken) =>
        _inner.TrimConversationMessagesAsync(conversationKey, maxMessages, cancellationToken);

    public Task ClearConversationMessagesAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.ClearConversationMessagesAsync(conversationKey, cancellationToken);

    public Task<long?> GetLatestConversationMessageIdAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.GetLatestConversationMessageIdAsync(conversationKey, cancellationToken);

    public Task AppendRawEventsAsync(IEnumerable<RawEventEntry> events, CancellationToken cancellationToken) =>
        _inner.AppendRawEventsAsync(events, cancellationToken);

    public Task<IReadOnlyList<RawEventRecord>> GetRawEventsAsync(string conversationKey, int limit, CancellationToken cancellationToken) =>
        _inner.GetRawEventsAsync(conversationKey, limit, cancellationToken);

    public Task<int> GetRawEventCountAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.GetRawEventCountAsync(conversationKey, cancellationToken);

    public Task<IReadOnlyList<ProjectCapsuleMemory>> GetProjectCapsulesAsync(string conversationKey, int limit, CancellationToken cancellationToken) =>
        _inner.GetProjectCapsulesAsync(conversationKey, limit, cancellationToken);

    public Task<int> GetProjectCapsuleCountAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.GetProjectCapsuleCountAsync(conversationKey, cancellationToken);

    public Task<long?> GetProjectCapsuleLatestSourceEventIdAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.GetProjectCapsuleLatestSourceEventIdAsync(conversationKey, cancellationToken);

    public Task<DateTimeOffset?> GetProjectCapsuleLastUpdatedAtUtcAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.GetProjectCapsuleLastUpdatedAtUtcAsync(conversationKey, cancellationToken);

    public Task<ProjectCapsuleExtractionState?> GetProjectCapsuleExtractionStateAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.GetProjectCapsuleExtractionStateAsync(conversationKey, cancellationToken);

    public Task ClearProjectCapsulesAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.ClearProjectCapsulesAsync(conversationKey, cancellationToken);

    public Task ClearProjectCapsuleExtractionStateAsync(string conversationKey, CancellationToken cancellationToken) =>
        _inner.ClearProjectCapsuleExtractionStateAsync(conversationKey, cancellationToken);
}
