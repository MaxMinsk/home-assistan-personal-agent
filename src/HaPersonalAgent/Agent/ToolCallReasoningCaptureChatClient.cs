using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: per-run middleware, захватывающее reasoning_content модели из ответа и складывающее его по id tool_call.
/// Зачем: HPA-041 follow-up — вписать reasoning обратно в исходящий tool-шаг может только request-политика (raw JSON,
/// единственный слой, доходящий до провода), но взять его она может лишь из захваченного здесь. M.E.AI корректно парсит
/// нестандартное поле reasoning_content из ответа Moonshot в TextReasoningContent (проверено wire-тестом), поэтому
/// захват живёт на уровне M.E.AI, а вписывание — в политике; общий канал — ToolCallReasoningStore.
/// Как: после каждого НЕстримингового ответа для каждого assistant-сообщения кладёт его reasoning под КАЖДЫЙ его callId.
/// Стриминговый путь оставлен без захвата — там reasoning на провод не попадёт, но safety-fallback честно выключит
/// thinking на таком tool-шаге (регресса нет). Инжект без захвата просто не сработает и оставит поведение прежним.
/// </summary>
public sealed class ToolCallReasoningCaptureChatClient : DelegatingChatClient
{
    private readonly ToolCallReasoningStore _store;
    private readonly ILogger<ToolCallReasoningCaptureChatClient> _logger;

    public ToolCallReasoningCaptureChatClient(
        IChatClient innerClient,
        ToolCallReasoningStore store,
        ILogger<ToolCallReasoningCaptureChatClient> logger)
        : base(innerClient)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        CaptureReasoning(response.Messages);
        return response;
    }

    private void CaptureReasoning(IEnumerable<ChatMessage> messages)
    {
        var capturedToolCalls = 0;
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                continue;
            }

            var toolCalls = message.Contents.OfType<FunctionCallContent>().ToArray();
            if (toolCalls.Length == 0)
            {
                continue;
            }

            var reasoning = string.Join(
                Environment.NewLine,
                message.Contents
                    .OfType<TextReasoningContent>()
                    .Select(content => content.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            if (string.IsNullOrWhiteSpace(reasoning))
            {
                continue;
            }

            foreach (var toolCall in toolCalls)
            {
                if (!string.IsNullOrWhiteSpace(toolCall.CallId))
                {
                    _store.Capture(toolCall.CallId, reasoning);
                    capturedToolCalls++;
                }
            }
        }

        if (capturedToolCalls > 0)
        {
            _logger.LogInformation(
                "Captured reasoning_content for {ToolCallCount} tool call(s) to replay on the next tool step (keeps thinking on).",
                capturedToolCalls);
        }
    }
}
