using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: per-run middleware для capture+replay reasoning content между tool-call шагами.
/// Зачем: некоторые OpenAI-compatible providers (например Moonshot/Kimi) требуют вернуть reasoning metadata в следующем tool-step запросе, иначе отвечают 400.
/// Как: после каждого ответа сохраняет TextReasoningContent для assistant tool-call сообщения и перед следующим вызовом добавляет его обратно в matching assistant tool-call history message.
/// </summary>
public sealed class ReasoningContentReplayChatClient : DelegatingChatClient
{
    private readonly Dictionary<string, string> _reasoningByToolSignature = new(StringComparer.Ordinal);
    private readonly ILogger<ReasoningContentReplayChatClient> _logger;

    public ReasoningContentReplayChatClient(
        IChatClient innerClient,
        ILogger<ReasoningContentReplayChatClient> logger)
        : base(innerClient)
    {
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var replayReadyMessages = CloneMessages(messages);
        var (requestToolCallMessageCount, requestMissingReasoningCount) =
            CountAssistantToolCallReasoningMessages(replayReadyMessages);
        if (requestToolCallMessageCount > 0)
        {
            _logger.LogInformation(
                "Reasoning replay middleware request diagnostics: assistant tool-call messages {ToolCallMessageCount}, missing reasoning {MissingReasoningCount}, cache entries {CacheEntryCount}.",
                requestToolCallMessageCount,
                requestMissingReasoningCount,
                _reasoningByToolSignature.Count);
        }

        var replayedCount = ReplayReasoningContent(replayReadyMessages);
        if (replayedCount > 0)
        {
            _logger.LogInformation(
                "Reasoning replay middleware injected reasoning content into {ReplayedMessageCount} assistant tool-call history messages.",
                replayedCount);
        }
        else if (requestMissingReasoningCount > 0)
        {
            _logger.LogWarning(
                "Reasoning replay middleware could not inject reasoning content into {MissingReasoningCount} assistant tool-call history messages; provider may reject this tool step without request-level fallback.",
                requestMissingReasoningCount);
        }

        var response = await base.GetResponseAsync(replayReadyMessages, options, cancellationToken);
        var (responseToolCallMessageCount, responseMissingReasoningCount) =
            CountAssistantToolCallReasoningMessages(response.Messages);
        var capturedCount = CaptureReasoningContent(response);
        if (responseToolCallMessageCount > 0)
        {
            _logger.LogInformation(
                "Reasoning replay middleware response diagnostics: assistant tool-call messages {ToolCallMessageCount}, captured reasoning {CapturedMessageCount}, missing reasoning {MissingReasoningCount}, cache entries {CacheEntryCount}.",
                responseToolCallMessageCount,
                capturedCount,
                responseMissingReasoningCount,
                _reasoningByToolSignature.Count);
        }
        if (responseToolCallMessageCount > 0 && capturedCount == 0)
        {
            _logger.LogWarning(
                "Reasoning replay middleware did not capture reasoning content from provider response with assistant tool-call messages.");
        }

        return response;
    }

    private int CaptureReasoningContent(ChatResponse response)
    {
        var capturedCount = 0;
        foreach (var message in response.Messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                continue;
            }

            var signature = TryCreateToolSignature(message);
            if (signature is null)
            {
                continue;
            }

            var reasoningText = string.Join(
                Environment.NewLine,
                message.Contents
                    .OfType<TextReasoningContent>()
                    .Select(content => content.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            if (string.IsNullOrWhiteSpace(reasoningText))
            {
                continue;
            }

            _reasoningByToolSignature[signature] = reasoningText;
            capturedCount++;
        }

        return capturedCount;
    }

    private int ReplayReasoningContent(IList<ChatMessage> messages)
    {
        var replayedCount = 0;
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                continue;
            }

            if (message.Contents.Any(content => content is TextReasoningContent))
            {
                continue;
            }

            var signature = TryCreateToolSignature(message);
            if (signature is null
                || !_reasoningByToolSignature.TryGetValue(signature, out var reasoningText)
                || string.IsNullOrWhiteSpace(reasoningText))
            {
                continue;
            }

            message.Contents.Add(new TextReasoningContent(reasoningText));
            replayedCount++;
        }

        return replayedCount;
    }

    private static List<ChatMessage> CloneMessages(IEnumerable<ChatMessage> messages)
    {
        var clonedMessages = new List<ChatMessage>();
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);

            var clone = message.Clone();
            clone.Contents = clone.Contents is { Count: > 0 }
                ? new List<AIContent>(clone.Contents)
                : new List<AIContent>();
            clonedMessages.Add(clone);
        }

        return clonedMessages;
    }

    private static (int ToolCallMessageCount, int MissingReasoningCount) CountAssistantToolCallReasoningMessages(
        IEnumerable<ChatMessage> messages)
    {
        var toolCallMessageCount = 0;
        var missingReasoningCount = 0;

        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                continue;
            }

            if (!message.Contents.OfType<FunctionCallContent>().Any())
            {
                continue;
            }

            toolCallMessageCount++;

            var hasReasoning = message.Contents
                .OfType<TextReasoningContent>()
                .Any(content => !string.IsNullOrWhiteSpace(content.Text));
            if (!hasReasoning)
            {
                missingReasoningCount++;
            }
        }

        return (toolCallMessageCount, missingReasoningCount);
    }

    private static string? TryCreateToolSignature(ChatMessage message)
    {
        var functionCalls = message.Contents
            .OfType<FunctionCallContent>()
            .ToArray();
        if (functionCalls.Length == 0)
        {
            return null;
        }

        var parts = functionCalls
            .Select(CreateFunctionCallSignaturePart)
            .OrderBy(part => part, StringComparer.Ordinal)
            .ToArray();
        if (parts.Length == 0)
        {
            return null;
        }

        return string.Join(";", parts);
    }

    private static string CreateFunctionCallSignaturePart(FunctionCallContent functionCall)
    {
        if (!string.IsNullOrWhiteSpace(functionCall.CallId))
        {
            return $"id:{functionCall.CallId.Trim()}";
        }

        var name = string.IsNullOrWhiteSpace(functionCall.Name)
            ? "unknown"
            : functionCall.Name.Trim();
        var argumentParts = (functionCall.Arguments ?? new Dictionary<string, object?>())
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}={entry.Value}")
            .ToArray();

        return argumentParts.Length == 0
            ? $"name:{name}"
            : $"name:{name}|args:{string.Join(",", argumentParts)}";
    }
}
