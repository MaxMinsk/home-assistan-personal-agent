using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: per-run middleware для подробного технического логирования каждого LLM запроса.
/// Зачем: в function loop один user turn может порождать несколько запросов к модели, и для диагностики thinking/reasoning поведения нужен детальный trace по каждому шагу.
/// Как: оборачивает IChatClient, считает sequence номер request внутри run и логирует request/response с метаданными роли сообщений, tool-call/reasoning признаками и token usage.
/// </summary>
public sealed class LlmRequestLoggingChatClient : DelegatingChatClient
{
    private readonly string _correlationId;
    private readonly LlmExecutionPlan _executionPlan;
    private readonly ILogger<LlmRequestLoggingChatClient> _logger;
    private int _requestSequence;

    public LlmRequestLoggingChatClient(
        IChatClient innerClient,
        string correlationId,
        LlmExecutionPlan executionPlan,
        ILogger<LlmRequestLoggingChatClient> logger)
        : base(innerClient)
    {
        _correlationId = string.IsNullOrWhiteSpace(correlationId)
            ? "unknown"
            : correlationId;
        _executionPlan = executionPlan ?? throw new ArgumentNullException(nameof(executionPlan));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var requestNumber = Interlocked.Increment(ref _requestSequence);
        var requestMessages = messages.ToList();
        var requestDiagnostics = AnalyzeMessages(requestMessages);
        var optionsToolCount = options?.Tools?.Count ?? 0;
        var instructionsChars = options?.Instructions?.Length ?? 0;
        var toolDefinitionChars = EstimateToolDefinitionChars(options?.Tools);
        var estimatedInputTokens = EstimateTokens(
            instructionsChars
            + toolDefinitionChars
            + requestDiagnostics.MessagePayloadChars);
        var staticPrefixHash = CreateStaticPrefixHash(options);
        var leadingSystemHash = CreateLeadingSystemHash(options, requestMessages);

        _logger.LogInformation(
            "LLM request {CorrelationId}#{RequestNumber} starting: profile {ExecutionProfile}, provider profile {ProviderProfile}, thinking requested {RequestedThinkingMode}, thinking effective {EffectiveThinkingMode}, patch pipeline enabled {PatchPipelineEnabled}, options tools {OptionsToolCount}, instructions chars {InstructionsChars}, tool definition chars {ToolDefinitionChars}, message payload chars {MessagePayloadChars}, leading system chars {LeadingSystemChars}, tool result chars {ToolResultChars}, reasoning chars replayed {ReasoningChars}, function argument chars {FunctionArgumentChars}, estimated input tokens {EstimatedInputTokens}, static prefix hash {StaticPrefixHash}, leading system hash {LeadingSystemHash}, messages total {MessageCount}, system {SystemMessageCount}, user {UserMessageCount}, assistant {AssistantMessageCount}, tool {ToolMessageCount}, assistant tool-call messages {AssistantToolCallMessageCount}, assistant reasoning messages {AssistantReasoningMessageCount}, assistant tool-call messages missing reasoning {MissingReasoningCount}.",
            _correlationId,
            requestNumber,
            _executionPlan.Profile,
            _executionPlan.Capabilities.ProviderKey,
            _executionPlan.RequestedThinkingMode,
            _executionPlan.EffectiveThinkingMode,
            _executionPlan.ShouldPatchChatCompletionRequest,
            optionsToolCount,
            instructionsChars,
            toolDefinitionChars,
            requestDiagnostics.MessagePayloadChars,
            requestDiagnostics.LeadingSystemChars,
            requestDiagnostics.ToolResultChars,
            requestDiagnostics.ReasoningChars,
            requestDiagnostics.FunctionArgumentChars,
            estimatedInputTokens,
            staticPrefixHash,
            leadingSystemHash,
            requestDiagnostics.MessageCount,
            requestDiagnostics.SystemMessageCount,
            requestDiagnostics.UserMessageCount,
            requestDiagnostics.AssistantMessageCount,
            requestDiagnostics.ToolMessageCount,
            requestDiagnostics.AssistantToolCallMessageCount,
            requestDiagnostics.AssistantReasoningMessageCount,
            requestDiagnostics.AssistantToolCallMissingReasoningCount);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.GetResponseAsync(requestMessages, options, cancellationToken);
            stopwatch.Stop();

            var responseDiagnostics = AnalyzeMessages(response.Messages);
            var reasoningTokenCount = TryGetAdditionalCount(
                response.Usage,
                "OutputTokenDetails.ReasoningTokenCount");
            var cachedInputTokenCount = TryGetAdditionalCount(
                response.Usage,
                "InputTokenDetails.CachedTokenCount");
            var cachedInputRatio = CalculateRatio(
                cachedInputTokenCount,
                response.Usage?.InputTokenCount);

            _logger.LogInformation(
                "LLM request {CorrelationId}#{RequestNumber} completed in {DurationMs} ms: response messages {ResponseMessageCount}, assistant {AssistantMessageCount}, assistant tool-call messages {AssistantToolCallMessageCount}, assistant reasoning messages {AssistantReasoningMessageCount}, assistant tool-call messages missing reasoning {MissingReasoningCount}, response text length {ResponseTextLength}, input tokens {InputTokenCount}, cached input tokens {CachedInputTokenCount}, cached input ratio {CachedInputRatio}, output tokens {OutputTokenCount}, total tokens {TotalTokenCount}, reasoning tokens {ReasoningTokenCount}.",
                _correlationId,
                requestNumber,
                stopwatch.ElapsedMilliseconds,
                responseDiagnostics.MessageCount,
                responseDiagnostics.AssistantMessageCount,
                responseDiagnostics.AssistantToolCallMessageCount,
                responseDiagnostics.AssistantReasoningMessageCount,
                responseDiagnostics.AssistantToolCallMissingReasoningCount,
                response.Text?.Length ?? 0,
                response.Usage?.InputTokenCount,
                cachedInputTokenCount,
                cachedInputRatio,
                response.Usage?.OutputTokenCount,
                response.Usage?.TotalTokenCount,
                reasoningTokenCount);

            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                exception,
                "LLM request {CorrelationId}#{RequestNumber} failed in {DurationMs} ms.",
                _correlationId,
                requestNumber,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var requestNumber = Interlocked.Increment(ref _requestSequence);
        var requestMessages = messages.ToList();
        var requestDiagnostics = AnalyzeMessages(requestMessages);
        var optionsToolCount = options?.Tools?.Count ?? 0;
        var instructionsChars = options?.Instructions?.Length ?? 0;
        var toolDefinitionChars = EstimateToolDefinitionChars(options?.Tools);
        var estimatedInputTokens = EstimateTokens(
            instructionsChars
            + toolDefinitionChars
            + requestDiagnostics.MessagePayloadChars);
        var staticPrefixHash = CreateStaticPrefixHash(options);
        var leadingSystemHash = CreateLeadingSystemHash(options, requestMessages);

        _logger.LogInformation(
            "LLM streaming request {CorrelationId}#{RequestNumber} starting: profile {ExecutionProfile}, provider profile {ProviderProfile}, thinking requested {RequestedThinkingMode}, thinking effective {EffectiveThinkingMode}, patch pipeline enabled {PatchPipelineEnabled}, options tools {OptionsToolCount}, instructions chars {InstructionsChars}, tool definition chars {ToolDefinitionChars}, message payload chars {MessagePayloadChars}, leading system chars {LeadingSystemChars}, tool result chars {ToolResultChars}, reasoning chars replayed {ReasoningChars}, function argument chars {FunctionArgumentChars}, estimated input tokens {EstimatedInputTokens}, static prefix hash {StaticPrefixHash}, leading system hash {LeadingSystemHash}, messages total {MessageCount}, system {SystemMessageCount}, user {UserMessageCount}, assistant {AssistantMessageCount}, tool {ToolMessageCount}, assistant tool-call messages {AssistantToolCallMessageCount}, assistant reasoning messages {AssistantReasoningMessageCount}, assistant tool-call messages missing reasoning {MissingReasoningCount}.",
            _correlationId,
            requestNumber,
            _executionPlan.Profile,
            _executionPlan.Capabilities.ProviderKey,
            _executionPlan.RequestedThinkingMode,
            _executionPlan.EffectiveThinkingMode,
            _executionPlan.ShouldPatchChatCompletionRequest,
            optionsToolCount,
            instructionsChars,
            toolDefinitionChars,
            requestDiagnostics.MessagePayloadChars,
            requestDiagnostics.LeadingSystemChars,
            requestDiagnostics.ToolResultChars,
            requestDiagnostics.ReasoningChars,
            requestDiagnostics.FunctionArgumentChars,
            estimatedInputTokens,
            staticPrefixHash,
            leadingSystemHash,
            requestDiagnostics.MessageCount,
            requestDiagnostics.SystemMessageCount,
            requestDiagnostics.UserMessageCount,
            requestDiagnostics.AssistantMessageCount,
            requestDiagnostics.ToolMessageCount,
            requestDiagnostics.AssistantToolCallMessageCount,
            requestDiagnostics.AssistantReasoningMessageCount,
            requestDiagnostics.AssistantToolCallMissingReasoningCount);

        var stopwatch = Stopwatch.StartNew();
        var updateCount = 0;
        var textDeltaLength = 0;
        var reasoningDeltaCount = 0;
        var toolCallUpdateCount = 0;

        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(requestMessages, options, cancellationToken).WithCancellation(cancellationToken))
            {
                updateCount++;
                textDeltaLength += update.Text?.Length ?? 0;
                if (update.Contents.Any(content => content is TextReasoningContent))
                {
                    reasoningDeltaCount++;
                }

                if (update.Contents.Any(content => content is FunctionCallContent))
                {
                    toolCallUpdateCount++;
                }

                yield return update;
            }
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogInformation(
                "LLM streaming request {CorrelationId}#{RequestNumber} completed in {DurationMs} ms: updates {UpdateCount}, text delta length {TextDeltaLength}, updates with reasoning {ReasoningDeltaCount}, updates with tool calls {ToolCallUpdateCount}.",
                _correlationId,
                requestNumber,
                stopwatch.ElapsedMilliseconds,
                updateCount,
                textDeltaLength,
                reasoningDeltaCount,
                toolCallUpdateCount);
        }
    }

    private static long? TryGetAdditionalCount(UsageDetails? usage, string key)
    {
        if (usage?.AdditionalCounts is null)
        {
            return null;
        }

        return usage.AdditionalCounts.TryGetValue(key, out var value)
            ? value
            : null;
    }

    private static MessageDiagnostics AnalyzeMessages(IEnumerable<ChatMessage> messages)
    {
        var diagnostics = new MessageDiagnostics
        {
            OnlyLeadingSystemMessagesSeen = true,
        };

        foreach (var message in messages)
        {
            diagnostics.MessageCount++;
            AnalyzeMessagePayload(message, ref diagnostics);
            if (diagnostics.OnlyLeadingSystemMessagesSeen && message.Role == ChatRole.System)
            {
                diagnostics.LeadingSystemChars += EstimateMessagePayloadChars(message);
            }
            else
            {
                diagnostics.OnlyLeadingSystemMessagesSeen = false;
            }
            switch (message.Role)
            {
                case var role when role == ChatRole.System:
                    diagnostics.SystemMessageCount++;
                    break;
                case var role when role == ChatRole.User:
                    diagnostics.UserMessageCount++;
                    break;
                case var role when role == ChatRole.Assistant:
                    diagnostics.AssistantMessageCount++;
                    AnalyzeAssistantMessage(message, ref diagnostics);
                    break;
                case var role when role == ChatRole.Tool:
                    diagnostics.ToolMessageCount++;
                    break;
            }
        }

        return diagnostics;
    }

    private static void AnalyzeAssistantMessage(
        ChatMessage message,
        ref MessageDiagnostics diagnostics)
    {
        var hasToolCall = false;
        var hasReasoning = false;

        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent)
            {
                hasToolCall = true;
            }

            if (content is TextReasoningContent reasoning
                && !string.IsNullOrWhiteSpace(reasoning.Text))
            {
                hasReasoning = true;
            }
        }

        if (hasToolCall)
        {
            diagnostics.AssistantToolCallMessageCount++;
        }

        if (hasReasoning)
        {
            diagnostics.AssistantReasoningMessageCount++;
        }

        if (hasToolCall && !hasReasoning)
        {
            diagnostics.AssistantToolCallMissingReasoningCount++;
        }
    }

    private static void AnalyzeMessagePayload(
        ChatMessage message,
        ref MessageDiagnostics diagnostics)
    {
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning:
                    var reasoningLength = reasoning.Text?.Length ?? 0;
                    diagnostics.ReasoningChars += reasoningLength;
                    diagnostics.MessagePayloadChars += reasoningLength;
                    break;
                case TextContent text:
                    diagnostics.MessagePayloadChars += text.Text?.Length ?? 0;
                    break;
                case FunctionCallContent call:
                    var argumentsLength = SerializeValue(call.Arguments).Length;
                    diagnostics.FunctionArgumentChars += argumentsLength;
                    diagnostics.MessagePayloadChars += argumentsLength;
                    break;
                case FunctionResultContent result:
                    var resultLength = SerializeValue(result.Result).Length;
                    diagnostics.ToolResultChars += resultLength;
                    diagnostics.MessagePayloadChars += resultLength;
                    break;
            }
        }
    }

    private static int EstimateMessagePayloadChars(ChatMessage message)
    {
        var diagnostics = new MessageDiagnostics();
        AnalyzeMessagePayload(message, ref diagnostics);
        return diagnostics.MessagePayloadChars;
    }

    private static int EstimateToolDefinitionChars(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var function in tools.OfType<AIFunction>())
        {
            total += function.Name?.Length ?? 0;
            total += function.Description?.Length ?? 0;
            total += function.JsonSchema.ToString().Length;
        }

        return total;
    }

    private static string CreateStaticPrefixHash(ChatOptions? options)
    {
        var builder = new StringBuilder(options?.Instructions ?? string.Empty);
        if (options?.Tools is not null)
        {
            foreach (var function in options.Tools
                         .OfType<AIFunction>()
                         .OrderBy(tool => tool.Name, StringComparer.Ordinal))
            {
                builder.Append('\n');
                builder.Append(function.Name);
                builder.Append('\n');
                builder.Append(function.Description);
                builder.Append('\n');
                builder.Append(function.JsonSchema);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 6));
    }

    private static string CreateLeadingSystemHash(
        ChatOptions? options,
        IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder(options?.Instructions ?? string.Empty);
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.System)
            {
                break;
            }

            builder.Append('\n');
            foreach (var content in message.Contents)
            {
                builder.Append(content switch
                {
                    TextContent text => text.Text,
                    TextReasoningContent reasoning => reasoning.Text,
                    FunctionCallContent call => SerializeValue(call.Arguments),
                    FunctionResultContent result => SerializeValue(result.Result),
                    _ => content.ToString(),
                });
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 6));
    }

    private static string SerializeValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return text;
        }

        if (value is JsonElement element)
        {
            return element.GetRawText();
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static int EstimateTokens(int chars) =>
        chars <= 0 ? 0 : (int)Math.Ceiling(chars / 4d);

    private static double? CalculateRatio(long? numerator, long? denominator)
    {
        if (!numerator.HasValue || !denominator.HasValue || denominator.Value <= 0)
        {
            return null;
        }

        return Math.Round(numerator.Value / (double)denominator.Value, 3);
    }

    private struct MessageDiagnostics
    {
        public int MessageCount;
        public int SystemMessageCount;
        public int UserMessageCount;
        public int AssistantMessageCount;
        public int ToolMessageCount;
        public int AssistantToolCallMessageCount;
        public int AssistantReasoningMessageCount;
        public int AssistantToolCallMissingReasoningCount;
        public int MessagePayloadChars;
        public int ToolResultChars;
        public int ReasoningChars;
        public int FunctionArgumentChars;
        public int LeadingSystemChars;
        public bool OnlyLeadingSystemMessagesSeen;
    }
}
