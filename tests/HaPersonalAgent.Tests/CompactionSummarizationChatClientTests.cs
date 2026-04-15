using HaPersonalAgent.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты диагностической обертки summarize-клиента для MAF compaction.
/// Зачем: HAAG-034 требует надежно понимать, запускался ли summarize step, и использовать этот сигнал в runtime.
/// Как: запускает обертку на fake IChatClient и проверяет счетчики запросов/ответов в <see cref="CompactionRunDiagnostics"/>.
/// </summary>
public class CompactionSummarizationChatClientTests
{
    [Fact]
    public async Task Middleware_records_non_streaming_summarization_request_and_response()
    {
        var diagnostics = new CompactionRunDiagnostics();
        var middleware = new CompactionSummarizationChatClient(
            new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary"))),
            correlationId: "test-run-1",
            diagnostics,
            LoggerFactory.Create(_ => { }).CreateLogger<CompactionSummarizationChatClient>());

        _ = await middleware.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "compress") },
            options: null,
            cancellationToken: CancellationToken.None);

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(1, snapshot.SummarizationRequests);
        Assert.Equal(1, snapshot.SummarizationResponses);
        Assert.True(snapshot.SummarizationTriggered);
    }

    [Fact]
    public async Task Middleware_records_streaming_summarization_request_and_response()
    {
        var diagnostics = new CompactionRunDiagnostics();
        var middleware = new CompactionSummarizationChatClient(
            new FakeChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary"))),
            correlationId: "test-run-2",
            diagnostics,
            LoggerFactory.Create(_ => { }).CreateLogger<CompactionSummarizationChatClient>());

        await foreach (var _ in middleware.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "compress") },
            options: null,
            cancellationToken: CancellationToken.None))
        {
        }

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(1, snapshot.SummarizationRequests);
        Assert.Equal(1, snapshot.SummarizationResponses);
        Assert.True(snapshot.SummarizationTriggered);
    }

    /// <summary>
    /// Что: минимальный fake IChatClient для тестов summarize middleware.
    /// Зачем: тесты должны быть deterministic и не зависеть от внешнего LLM provider.
    /// Как: возвращает заранее заданный ответ и пустой streaming-поток.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly ChatResponse _response;

        public FakeChatClient(ChatResponse response)
        {
            _response = response;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            EmptyUpdates();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
