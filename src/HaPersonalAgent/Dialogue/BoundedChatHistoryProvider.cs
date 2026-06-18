using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// What: bounded recent-window provider whose long-term recall is served by Memory MCP (HPA-005).
/// Why: the short window of recent turns stays in conversation_messages; the local hash-vector store is
/// retired, so long-term recall now comes from Memory MCP (durable notes in the `home` domain) when configured.
/// How: loads the recent window from the local store, optionally injects a recall block from a Memory MCP
/// notes_search (best-effort, bounded timeout, never throws), and trims the window after a turn. The rolling
/// summary remains the always-present long-term context.
/// References: MAF AgentWithMemory_Step05_BoundedChatHistory (bounded window) + Agent_Step22_MemorySearch (recall layer).
/// </summary>
public sealed class BoundedChatHistoryProvider
{
    private const int DefaultRecallTopK = 4;
    private static readonly TimeSpan RecallTimeout = TimeSpan.FromSeconds(4);

    private readonly IConversationMemoryStore _memoryStore;
    private readonly IMemoryMcpClient? _memoryMcpClient;
    private readonly IOptions<MemoryMcpOptions>? _memoryMcpOptions;
    private readonly ILogger<BoundedChatHistoryProvider> _logger;

    public BoundedChatHistoryProvider(
        IConversationMemoryStore memoryStore,
        ILogger<BoundedChatHistoryProvider> logger,
        IMemoryMcpClient? memoryMcpClient = null,
        IOptions<MemoryMcpOptions>? memoryMcpOptions = null)
    {
        _memoryStore = memoryStore;
        _logger = logger;
        _memoryMcpClient = memoryMcpClient;
        _memoryMcpOptions = memoryMcpOptions;
    }

    public async Task<BoundedChatHistorySnapshot> LoadAsync(
        string conversationKey,
        string userMessage,
        int maxRecentMessages,
        bool includeRetrievedMemory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        var recentMessages = await _memoryStore.GetConversationMessagesAsync(
            conversationKey,
            maxRecentMessages,
            cancellationToken);

        var (contextText, retrievedCount) = includeRetrievedMemory
            ? await RecallFromMemoryMcpAsync(userMessage, cancellationToken)
            : (null, 0);

        _logger.LogInformation(
            "Bounded chat history load for {ConversationKey}: recent messages {RecentMessages}, retrieval mode {RetrievalMode}, retrieved memories {RetrievedMemories}.",
            conversationKey,
            recentMessages.Count,
            includeRetrievedMemory ? "before_invoke" : "on_demand_tool",
            retrievedCount);

        return new BoundedChatHistorySnapshot(recentMessages, contextText, retrievedCount);
    }

    /// <summary>
    /// Trim the conversation window down to the most recent messages after a turn. Overflow is captured by
    /// the rolling summary; the retired local vector store no longer archives it.
    /// </summary>
    public async Task TrimOverflowAsync(
        string conversationKey,
        int maxRecentMessages,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        await _memoryStore.TrimConversationMessagesAsync(conversationKey, maxRecentMessages, cancellationToken);
    }

    private async Task<(string? Context, int Count)> RecallFromMemoryMcpAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (_memoryMcpClient is null
            || _memoryMcpOptions?.Value.IsConfigured != true
            || string.IsNullOrWhiteSpace(query))
        {
            return (null, 0);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RecallTimeout);

            var result = await _memoryMcpClient.CallToolAsync(
                "notes_search",
                new Dictionary<string, object?>
                {
                    ["domain"] = "home",
                    ["query"] = query,
                    ["tags"] = new[] { "ha-personal-agent" },
                    ["limit"] = DefaultRecallTopK,
                },
                timeout.Token);

            if (result.IsError || string.IsNullOrWhiteSpace(result.Text))
            {
                return (null, 0);
            }

            var count = CountHits(result.Text);
            if (count == 0)
            {
                return (null, 0);
            }

            var context =
                "Relevant durable memory from the long-term store (Memory MCP). Use as supporting context; prioritize the latest turns and explicit user corrections.\n"
                + result.Text;
            return (context, count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Recall is best-effort: a Memory MCP outage or timeout must never break a dialogue turn.
            _logger.LogWarning(exception, "Memory MCP recall failed; continuing without long-term recall.");
            return (null, 0);
        }
    }

    private static int CountHits(string searchResultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(searchResultJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    return items.GetArrayLength();
                }

                if (root.TryGetProperty("total", out var total) && total.TryGetInt32(out var totalValue))
                {
                    return totalValue;
                }
            }
        }
        catch (JsonException)
        {
        }

        return 1;
    }
}
