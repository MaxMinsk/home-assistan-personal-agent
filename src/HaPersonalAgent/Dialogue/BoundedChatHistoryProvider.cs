using System.Globalization;
using System.Text;
using HaPersonalAgent.Agent;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: bounded chat history provider с overflow в локальную vector memory.
/// Зачем: держим короткое окно recent turns в conversation_messages, а вытесненные сообщения архивируем и семантически достаем при релевантном запросе.
/// Как: реализует MAF Step05 паттерн (BoundedChatHistory + overflow retrieval), но как адаптер поверх нашей SQLite схемы.
/// Ссылки:
/// - https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentWithMemory/AgentWithMemory_Step05_BoundedChatHistory/BoundedChatHistoryProvider.cs
/// - https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentWithMemory/AgentWithMemory_Step05_BoundedChatHistory/Program.cs
/// </summary>
public sealed class BoundedChatHistoryProvider
{
    private const int EmbeddingDimensions = 128;
    private const int DefaultRecallTopK = 4;
    private const int DefaultSearchLimit = 1200;
    private const float SimilarityThreshold = 0.30f;
    private const int MemorySnippetMaxLength = 220;

    private readonly ILogger<BoundedChatHistoryProvider> _logger;
    private readonly AgentStateRepository _stateRepository;

    public BoundedChatHistoryProvider(
        AgentStateRepository stateRepository,
        ILogger<BoundedChatHistoryProvider> logger)
    {
        _stateRepository = stateRepository;
        _logger = logger;
    }

    public async Task<BoundedChatHistorySnapshot> LoadAsync(
        string conversationKey,
        string userMessage,
        int maxRecentMessages,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        var recentMessages = await _stateRepository.GetConversationMessagesAsync(
            conversationKey,
            maxRecentMessages,
            cancellationToken);
        var retrieved = await RetrieveMemoriesAsync(
            conversationKey,
            userMessage,
            cancellationToken);
        var contextText = BuildRetrievedMemoryContext(retrieved);

        _logger.LogInformation(
            "Bounded chat history load for {ConversationKey}: recent messages {RecentMessages}, retrieved memories {RetrievedMemories}.",
            conversationKey,
            recentMessages.Count,
            retrieved.Count);

        return new BoundedChatHistorySnapshot(
            recentMessages,
            contextText,
            retrieved.Count);
    }

    public async Task ArchiveOverflowAndTrimAsync(
        string conversationKey,
        int maxRecentMessages,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        var overflowMessages = await _stateRepository.GetOverflowConversationMessagesAsync(
            conversationKey,
            maxRecentMessages,
            cancellationToken);
        if (overflowMessages.Count == 0)
        {
            return;
        }

        var vectorEntries = new List<ConversationVectorMemoryEntry>(overflowMessages.Count);
        foreach (var message in overflowMessages)
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            var embedding = BuildEmbedding(message.Text);
            if (!HasMeaningfulSignal(embedding))
            {
                continue;
            }

            vectorEntries.Add(new ConversationVectorMemoryEntry(
                conversationKey,
                message.Id,
                message.Role,
                message.Text,
                SerializeEmbedding(embedding),
                message.CreatedAtUtc));
        }

        await _stateRepository.UpsertConversationVectorMemoryAsync(
            vectorEntries,
            cancellationToken);
        await _stateRepository.TrimConversationMessagesAsync(
            conversationKey,
            maxRecentMessages,
            cancellationToken);

        _logger.LogInformation(
            "Bounded chat history archived overflow for {ConversationKey}: overflow messages {OverflowMessages}, archived vectors {ArchivedVectors}, max recent {MaxRecentMessages}.",
            conversationKey,
            overflowMessages.Count,
            vectorEntries.Count,
            maxRecentMessages);
    }

    private async Task<IReadOnlyList<RetrievedMemoryCandidate>> RetrieveMemoriesAsync(
        string conversationKey,
        string userMessage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return Array.Empty<RetrievedMemoryCandidate>();
        }

        var queryEmbedding = BuildEmbedding(userMessage);
        if (!HasMeaningfulSignal(queryEmbedding))
        {
            return Array.Empty<RetrievedMemoryCandidate>();
        }

        var vectorRecords = await _stateRepository.GetConversationVectorMemoryAsync(
            conversationKey,
            DefaultSearchLimit,
            cancellationToken);
        if (vectorRecords.Count == 0)
        {
            return Array.Empty<RetrievedMemoryCandidate>();
        }

        var candidates = new List<RetrievedMemoryCandidate>(capacity: Math.Min(vectorRecords.Count, 32));
        foreach (var record in vectorRecords)
        {
            var embedding = ParseEmbedding(record.Embedding);
            if (embedding is null)
            {
                continue;
            }

            var score = DotProduct(queryEmbedding, embedding);
            if (score < SimilarityThreshold)
            {
                continue;
            }

            candidates.Add(new RetrievedMemoryCandidate(
                record.SourceMessageId,
                record.Role,
                record.Content,
                score));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.SourceMessageId)
            .Take(DefaultRecallTopK)
            .ToArray();
    }

    private static string? BuildRetrievedMemoryContext(IReadOnlyList<RetrievedMemoryCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(
            """
            Older relevant memories from this conversation (retrieved from vector overflow).
            Use them as supporting context, but prioritize explicit user corrections and the latest turns.
            """);
        foreach (var candidate in candidates)
        {
            var normalizedText = NormalizeSnippet(candidate.Text);
            if (normalizedText.Length == 0)
            {
                continue;
            }

            builder.AppendLine();
            builder.Append("- [");
            builder.Append(candidate.Role == AgentConversationRole.User ? "user" : "assistant");
            builder.Append(" #");
            builder.Append(candidate.SourceMessageId.ToString(CultureInfo.InvariantCulture));
            builder.Append("] ");
            builder.Append(normalizedText);
        }

        return builder.ToString();
    }

    private static string NormalizeSnippet(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Trim();

        return normalized.Length <= MemorySnippetMaxLength
            ? normalized
            : normalized[..MemorySnippetMaxLength] + "...";
    }

    private static float[] BuildEmbedding(string text)
    {
        var vector = new float[EmbeddingDimensions];
        foreach (var token in Tokenize(text))
        {
            var hash = StableHash(token);
            var index = Math.Abs(hash % EmbeddingDimensions);
            vector[index] += 1f;
        }

        NormalizeInPlace(vector);
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var tokenBuilder = new StringBuilder(capacity: 32);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                tokenBuilder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (tokenBuilder.Length > 1)
            {
                yield return tokenBuilder.ToString();
            }

            tokenBuilder.Clear();
        }

        if (tokenBuilder.Length > 1)
        {
            yield return tokenBuilder.ToString();
        }
    }

    private static void NormalizeInPlace(float[] vector)
    {
        var norm = 0d;
        for (var index = 0; index < vector.Length; index++)
        {
            norm += vector[index] * vector[index];
        }

        if (norm <= 0d)
        {
            return;
        }

        var inverseNorm = 1f / (float)Math.Sqrt(norm);
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] *= inverseNorm;
        }
    }

    private static bool HasMeaningfulSignal(float[] vector)
    {
        for (var index = 0; index < vector.Length; index++)
        {
            if (vector[index] > 0f)
            {
                return true;
            }
        }

        return false;
    }

    private static string SerializeEmbedding(float[] vector) =>
        string.Join(
            ",",
            vector.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));

    private static float[]? ParseEmbedding(string rawEmbedding)
    {
        if (string.IsNullOrWhiteSpace(rawEmbedding))
        {
            return null;
        }

        var tokens = rawEmbedding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != EmbeddingDimensions)
        {
            return null;
        }

        var embedding = new float[EmbeddingDimensions];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!float.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            embedding[index] = value;
        }

        return embedding;
    }

    private static float DotProduct(float[] left, float[] right)
    {
        if (left.Length != right.Length)
        {
            return 0f;
        }

        var sum = 0f;
        for (var index = 0; index < left.Length; index++)
        {
            sum += left[index] * right[index];
        }

        return sum;
    }

    private static int StableHash(string token)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in token)
            {
                hash = (hash * 31) + character;
            }

            return hash;
        }
    }

    private sealed record RetrievedMemoryCandidate(
        long SourceMessageId,
        AgentConversationRole Role,
        string Text,
        float Score);
}
