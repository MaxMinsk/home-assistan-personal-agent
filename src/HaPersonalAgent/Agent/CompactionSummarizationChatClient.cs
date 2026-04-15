using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: диагностическая обертка для chat client, который используется SummarizationCompactionStrategy.
/// Зачем: нужно явно зафиксировать факт вызова summarize шага в compaction pipeline и передать этот сигнал выше в runtime/диалог.
/// Как: считает каждый запрос/ответ summarizer-клиента и пишет структурированные логи по correlation id.
/// Ссылка:
/// - https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step18_CompactionPipeline/Program.cs
/// </summary>
public sealed class CompactionSummarizationChatClient : DelegatingChatClient
{
    private readonly string _correlationId;
    private readonly CompactionRunDiagnostics _diagnostics;
    private readonly ILogger<CompactionSummarizationChatClient> _logger;

    public CompactionSummarizationChatClient(
        IChatClient innerClient,
        string correlationId,
        CompactionRunDiagnostics diagnostics,
        ILogger<CompactionSummarizationChatClient> logger)
        : base(innerClient)
    {
        _correlationId = string.IsNullOrWhiteSpace(correlationId)
            ? "unknown"
            : correlationId;
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _diagnostics.RecordSummarizationRequest();
        _logger.LogInformation(
            "Compaction summarization request started for run {CorrelationId}.",
            _correlationId);

        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        _diagnostics.RecordSummarizationResponse(response.Text);
        _logger.LogInformation(
            "Compaction summarization request completed for run {CorrelationId}; summary text length {SummaryLength}.",
            _correlationId,
            response.Text?.Length ?? 0);

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _diagnostics.RecordSummarizationRequest();
        _logger.LogInformation(
            "Compaction summarization streaming request started for run {CorrelationId}.",
            _correlationId);

        var updateCount = 0;
        var summaryText = new System.Text.StringBuilder();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).WithCancellation(cancellationToken))
        {
            updateCount++;
            if (!string.IsNullOrWhiteSpace(update.Text))
            {
                summaryText.Append(update.Text);
            }
            yield return update;
        }

        _diagnostics.RecordSummarizationResponse(summaryText.ToString());
        _logger.LogInformation(
            "Compaction summarization streaming request completed for run {CorrelationId}; update count {UpdateCount}.",
            _correlationId,
            updateCount);
    }
}
