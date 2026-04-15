using HaPersonalAgent.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты подробного логирования каждого LLM request внутри одного agent run.
/// Зачем: нужно гарантировать, что thinking режим и ключевые request/response diagnostics пишутся на каждый вызов модели, а не только на уровень всего run.
/// Как: оборачивает fake IChatClient в LlmRequestLoggingChatClient и проверяет сформированные log entries.
/// </summary>
public class LlmRequestLoggingChatClientTests
{
    [Fact]
    public async Task Middleware_logs_start_and_completion_for_each_request_with_thinking_details()
    {
        var responses = new[]
        {
            new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    new List<AIContent>
                    {
                        new FunctionCallContent("call-1", "hass_get_state", new Dictionary<string, object?>()),
                        new TextReasoningContent("reasoning"),
                    })),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")),
        };

        var loggerProvider = new ListLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));

        var middleware = new LlmRequestLoggingChatClient(
            new FakeChatClient(responses),
            correlationId: "run-42",
            executionPlan: CreateExecutionPlan(),
            logger: loggerFactory.CreateLogger<LlmRequestLoggingChatClient>());

        await middleware.GetResponseAsync(
            new[]
            {
                new ChatMessage(ChatRole.User, "сколько градусов на улице?"),
            },
            options: null,
            cancellationToken: CancellationToken.None);

        await middleware.GetResponseAsync(
            new[]
            {
                new ChatMessage(
                    ChatRole.Assistant,
                    new List<AIContent>
                    {
                        new FunctionCallContent("call-1", "hass_get_state", new Dictionary<string, object?>()),
                    }),
                new ChatMessage(
                    ChatRole.Tool,
                    new List<AIContent>
                    {
                        new FunctionResultContent("call-1", "{\"temperature\": 21}"),
                    }),
            },
            options: null,
            cancellationToken: CancellationToken.None);

        var logs = loggerProvider.Messages;
        Assert.Contains(logs, log => log.Contains("LLM request run-42#1 starting", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Contains("LLM request run-42#1 completed", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Contains("LLM request run-42#2 starting", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Contains("LLM request run-42#2 completed", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Contains("thinking requested auto", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs, log => log.Contains("thinking effective ProviderDefault", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Contains("assistant tool-call messages missing reasoning 1", StringComparison.Ordinal));
    }

    private static LlmExecutionPlan CreateExecutionPlan() =>
        new(
            Profile: LlmExecutionProfile.ToolEnabled,
            Capabilities: new LlmProviderCapabilities(
                ProviderKey: "moonshot",
                SupportsTools: true,
                SupportsStreaming: true,
                SupportsReasoning: true,
                RequiresReasoningContentRoundTripForToolCalls: true,
                SupportsReasoningContentRoundTrip: true,
                SupportsExplicitThinkingEnable: false,
                ThinkingControlStyle: LlmThinkingControlStyle.OpenAiCompatibleThinkingObject),
            RequestedThinkingMode: "auto",
            EffectiveThinkingMode: LlmEffectiveThinkingMode.ProviderDefault,
            Reason: "test");

    /// <summary>
    /// Что: fake IChatClient для проверки middleware логики без реального провайдера.
    /// Зачем: unit test должен быть deterministic и не зависеть от сети/LLM.
    /// Как: возвращает заранее заданные ChatResponse по очереди.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses;

        public FakeChatClient(IEnumerable<ChatResponse> responses)
        {
            _responses = new Queue<ChatResponse>(responses);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Что: in-memory logger provider для unit-тестов.
    /// Зачем: нужно ассертом проверить реальные сформированные log messages middleware.
    /// Как: хранит formatter output каждого Log вызова в список Messages.
    /// </summary>
    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new ListLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class ListLogger : ILogger
        {
            private readonly List<string> _messages;

            public ListLogger(List<string> messages)
            {
                _messages = messages;
            }

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull =>
                NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                _messages.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
