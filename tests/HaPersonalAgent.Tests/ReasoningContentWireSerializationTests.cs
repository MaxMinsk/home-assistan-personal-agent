using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: wire-level проверка гипотезы HPA-041 — доходит ли reasoning_content до провайдера.
/// Зачем: ReasoningContentReplayChatClient добавляет TextReasoningContent в assistant tool-call сообщение,
/// но если OpenAI-клиент M.E.AI не сериализует его в поле reasoning_content, то replay инертен на проводе,
/// а raw-JSON политика ВСЕГДА видит «missing reasoning_content» и глушит thinking на каждом tool-шаге.
/// Как: строим реальный OpenAI ChatClient с перехватывающим транспортом, отправляем историю с assistant
/// tool-call, у которого есть TextReasoningContent, и смотрим фактический исходящий JSON.
/// </summary>
public class ReasoningContentWireSerializationTests
{
    [Fact]
    public async Task Assistant_tool_call_reasoning_content_is_not_serialized_to_the_outbound_request()
    {
        var handler = new CapturingHandler();
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://api.moonshot.ai/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        };

        var chatClient = new ChatClient(
            "kimi-k2.6",
            new ApiKeyCredential("test-key"),
            options);
        IChatClient client = chatClient.AsIChatClient();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "поищи в вебе"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                // Ровно то, что кладёт ReasoningContentReplayChatClient.ReplayReasoningContent.
                new TextReasoningContent("REASONING_MARKER_that_should_reach_the_wire"),
                new FunctionCallContent(
                    "call_1",
                    "web_search",
                    new Dictionary<string, object?> { ["query"] = "минск" }),
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call_1", "{\"results\":[]}"),
            }),
            new(ChatRole.User, "и что нашёл?"),
        };

        await client.GetResponseAsync(messages);

        var body = handler.CapturedBody;
        Assert.NotNull(body);

        // Sanity: assistant tool-call действительно сериализовался как tool_calls — значит история дошла до сериализатора.
        Assert.Contains("tool_calls", body, StringComparison.Ordinal);

        // Гипотеза HPA-041: reasoning ни как поле reasoning_content, ни как текст на провод НЕ попадает.
        Assert.DoesNotContain("reasoning_content", body!, StringComparison.Ordinal);
        Assert.DoesNotContain("REASONING_MARKER_that_should_reach_the_wire", body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasoning_content_in_a_provider_response_is_surfaced_as_text_reasoning_content()
    {
        // HPA-041 follow-up: чтобы политика могла вписать reasoning_content в исходящий запрос и оставить
        // thinking включённым на tool-шагах, его сначала надо где-то ЗАХВАТИТЬ. Здесь проверяем, что
        // OpenAI-клиент M.E.AI парсит нестандартное поле reasoning_content из ответа Moonshot в TextReasoningContent
        // и что callId дошёл — это ключ, по которому потом инжектим reasoning на провод.
        const string responseWithReasoning = """
            {
              "id": "chatcmpl-test",
              "object": "chat.completion",
              "created": 0,
              "model": "kimi-k2.6",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "reasoning_content": "CAPTURED_REASONING_MARKER",
                    "tool_calls": [
                      {
                        "id": "call_probe",
                        "type": "function",
                        "function": { "name": "web_search", "arguments": "{\"query\":\"x\"}" }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """;

        var handler = new CapturingHandler(responseWithReasoning);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://api.moonshot.ai/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        };
        IChatClient client = new ChatClient("kimi-k2.6", new ApiKeyCredential("test-key"), options)
            .AsIChatClient();

        var response = await client.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "поищи в вебе") });

        var assistant = response.Messages.Single(m => m.Role == ChatRole.Assistant);
        var toolCall = assistant.Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call_probe", toolCall.CallId);

        var reasoning = assistant.Contents.OfType<TextReasoningContent>().Select(c => c.Text);
        Assert.Contains("CAPTURED_REASONING_MARKER", string.Join("\n", reasoning), StringComparison.Ordinal);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public CapturingHandler(string? responseBody = null)
        {
            _responseBody = responseBody ?? """
                {
                  "id": "chatcmpl-test",
                  "object": "chat.completion",
                  "created": 0,
                  "model": "kimi-k2.6",
                  "choices": [
                    {
                      "index": 0,
                      "message": { "role": "assistant", "content": "ок" },
                      "finish_reason": "stop"
                    }
                  ],
                  "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
                }
                """;
        }

        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
