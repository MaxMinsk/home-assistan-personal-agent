using System.Diagnostics;
using System.Runtime.CompilerServices;
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

        _logger.LogInformation(
            "LLM request {CorrelationId}#{RequestNumber} starting: profile {ExecutionProfile}, provider profile {ProviderProfile}, thinking requested {RequestedThinkingMode}, thinking effective {EffectiveThinkingMode}, patch pipeline enabled {PatchPipelineEnabled}, options tools {OptionsToolCount}, messages total {MessageCount}, system {SystemMessageCount}, user {UserMessageCount}, assistant {AssistantMessageCount}, tool {ToolMessageCount}, assistant tool-call messages {AssistantToolCallMessageCount}, assistant reasoning messages {AssistantReasoningMessageCount}, assistant tool-call messages missing reasoning {MissingReasoningCount}.",
            _correlationId,
            requestNumber,
            _executionPlan.Profile,
            _executionPlan.Capabilities.ProviderKey,
            _executionPlan.RequestedThinkingMode,
            _executionPlan.EffectiveThinkingMode,
            _executionPlan.ShouldPatchChatCompletionRequest,
            optionsToolCount,
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

            _logger.LogInformation(
                "LLM request {CorrelationId}#{RequestNumber} completed in {DurationMs} ms: response messages {ResponseMessageCount}, assistant {AssistantMessageCount}, assistant tool-call messages {AssistantToolCallMessageCount}, assistant reasoning messages {AssistantReasoningMessageCount}, assistant tool-call messages missing reasoning {MissingReasoningCount}, response text length {ResponseTextLength}, input tokens {InputTokenCount}, output tokens {OutputTokenCount}, total tokens {TotalTokenCount}, reasoning tokens {ReasoningTokenCount}.",
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

        _logger.LogInformation(
            "LLM streaming request {CorrelationId}#{RequestNumber} starting: profile {ExecutionProfile}, provider profile {ProviderProfile}, thinking requested {RequestedThinkingMode}, thinking effective {EffectiveThinkingMode}, patch pipeline enabled {PatchPipelineEnabled}, options tools {OptionsToolCount}, messages total {MessageCount}, system {SystemMessageCount}, user {UserMessageCount}, assistant {AssistantMessageCount}, tool {ToolMessageCount}, assistant tool-call messages {AssistantToolCallMessageCount}, assistant reasoning messages {AssistantReasoningMessageCount}, assistant tool-call messages missing reasoning {MissingReasoningCount}.",
            _correlationId,
            requestNumber,
            _executionPlan.Profile,
            _executionPlan.Capabilities.ProviderKey,
            _executionPlan.RequestedThinkingMode,
            _executionPlan.EffectiveThinkingMode,
            _executionPlan.ShouldPatchChatCompletionRequest,
            optionsToolCount,
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
        var diagnostics = new MessageDiagnostics();

        foreach (var message in messages)
        {
            diagnostics.MessageCount++;
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
    }
}
