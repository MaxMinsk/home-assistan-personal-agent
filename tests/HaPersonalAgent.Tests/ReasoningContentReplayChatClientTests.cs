using HaPersonalAgent.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты middleware для reasoning content replay между tool-call шагами.
/// Зачем: при thinking-enabled tool runs нужно доказать, что reasoning metadata хранится только в памяти run и подставляется обратно в assistant tool-call history.
/// Как: использует fake IChatClient, который возвращает заранее заданные ответы и сохраняет фактически отправленные сообщения.
/// </summary>
public class ReasoningContentReplayChatClientTests
{
    [Fact]
    public async Task Middleware_replays_reasoning_content_for_matching_tool_call_signature()
    {
        var capturedReasoningMessage = new ChatMessage(
            ChatRole.Assistant,
            new List<AIContent>
            {
                new FunctionCallContent("call-1", "hass_get_state", new Dictionary<string, object?>()),
                new TextReasoningContent("reasoning trace"),
            });

        var innerClient = new FakeChatClient(new[]
        {
            new ChatResponse(capturedReasoningMessage),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")),
        });
        var middleware = new ReasoningContentReplayChatClient(
            innerClient,
            LoggerFactory.Create(_ => { }).CreateLogger<ReasoningContentReplayChatClient>());

        await middleware.GetResponseAsync(
            new[]
            {
                new ChatMessage(ChatRole.User, "first"),
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
                        new FunctionResultContent("call-1", "{\"state\":\"22\"}"),
                    }),
            },
            options: null,
            cancellationToken: CancellationToken.None);

        var secondCallMessages = innerClient.Requests[1];
        var replayedAssistantMessage = secondCallMessages.Single(message => message.Role == ChatRole.Assistant);

        Assert.Contains(
            replayedAssistantMessage.Contents,
            content => content is TextReasoningContent reasoning
                && reasoning.Text == "reasoning trace");
    }

    [Fact]
    public async Task Middleware_does_not_replay_reasoning_for_different_tool_signature()
    {
        var capturedReasoningMessage = new ChatMessage(
            ChatRole.Assistant,
            new List<AIContent>
            {
                new FunctionCallContent("call-1", "hass_get_state", new Dictionary<string, object?>()),
                new TextReasoningContent("reasoning trace"),
            });

        var innerClient = new FakeChatClient(new[]
        {
            new ChatResponse(capturedReasoningMessage),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")),
        });
        var middleware = new ReasoningContentReplayChatClient(
            innerClient,
            LoggerFactory.Create(_ => { }).CreateLogger<ReasoningContentReplayChatClient>());

        await middleware.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "first") },
            options: null,
            cancellationToken: CancellationToken.None);
        await middleware.GetResponseAsync(
            new[]
            {
                new ChatMessage(
                    ChatRole.Assistant,
                    new List<AIContent>
                    {
                        new FunctionCallContent("call-2", "hass_get_state", new Dictionary<string, object?>()),
                    }),
                new ChatMessage(
                    ChatRole.Tool,
                    new List<AIContent>
                    {
                        new FunctionResultContent("call-2", "{\"state\":\"23\"}"),
                    }),
            },
            options: null,
            cancellationToken: CancellationToken.None);

        var secondCallMessages = innerClient.Requests[1];
        var replayedAssistantMessage = secondCallMessages.Single(message => message.Role == ChatRole.Assistant);

        Assert.DoesNotContain(replayedAssistantMessage.Contents, content => content is TextReasoningContent);
    }

    /// <summary>
    /// Что: fake IChatClient для deterministic middleware tests.
    /// Зачем: middleware должен проверяться без реального LLM провайдера и сетевых вызовов.
    /// Как: возвращает заранее заданные ChatResponse и сохраняет фактически полученные входные сообщения.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses;

        public FakeChatClient(IEnumerable<ChatResponse> responses)
        {
            _responses = new Queue<ChatResponse>(responses);
        }

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.Select(message =>
            {
                var clone = message.Clone();
                clone.Contents = clone.Contents is { Count: > 0 }
                    ? new List<AIContent>(clone.Contents)
                    : new List<AIContent>();
                return clone;
            }).ToArray();
            Requests.Add(snapshot);

            return Task.FromResult(_responses.Dequeue());
        }

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
