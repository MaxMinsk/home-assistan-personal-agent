using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты HPA-041 follow-up — reasoning_content захватывается из ответа и вписывается обратно на провод,
/// чтобы thinking оставался включённым во время работы с инструментами.
/// Зачем: это меняет фактическое поведение (модель думает между вызовами инструментов), поэтому граничные случаи
/// (есть/нет захвата, идемпотентность, отсутствие регресса к 400) фиксируем контрактом.
/// Как: capture-клиент проверяем на фейковом ответе; вписывание — на сыром JSON и через полный patch-пайплайн с реальным планом Moonshot.
/// </summary>
public class ToolCallReasoningInjectionTests
{
    [Fact]
    public void Injects_reasoning_content_from_the_store_into_a_tool_call_message_that_lacks_it()
    {
        var root = ParseRequest("""
            {
              "messages": [
                { "role": "assistant", "content": "", "tool_calls": [ { "id": "call_1", "type": "function" } ] },
                { "role": "tool", "content": "{}", "tool_call_id": "call_1" }
              ]
            }
            """);
        var store = new ToolCallReasoningStore();
        store.Capture("call_1", "модель рассуждала здесь");

        var changed = LlmChatCompletionRequestPolicy.TryInjectToolCallReasoning(root, store);

        Assert.True(changed);
        Assert.Equal(
            "модель рассуждала здесь",
            root["messages"]!.AsArray()[0]!["reasoning_content"]!.GetValue<string>());
    }

    [Fact]
    public void Existing_reasoning_content_is_not_overwritten()
    {
        var root = ParseRequest("""
            {
              "messages": [
                { "role": "assistant", "reasoning_content": "исходное", "tool_calls": [ { "id": "call_1" } ] }
              ]
            }
            """);
        var store = new ToolCallReasoningStore();
        store.Capture("call_1", "другое");

        Assert.False(LlmChatCompletionRequestPolicy.TryInjectToolCallReasoning(root, store));
        Assert.Equal("исходное", root["messages"]!.AsArray()[0]!["reasoning_content"]!.GetValue<string>());
    }

    [Fact]
    public void Nothing_is_injected_when_the_store_has_no_reasoning_for_the_tool_call()
    {
        var root = ParseRequest("""
            {
              "messages": [
                { "role": "assistant", "tool_calls": [ { "id": "call_unknown" } ] }
              ]
            }
            """);

        Assert.False(LlmChatCompletionRequestPolicy.TryInjectToolCallReasoning(root, new ToolCallReasoningStore()));
    }

    [Fact]
    public void With_captured_reasoning_thinking_stays_on_and_the_safety_fallback_does_not_fire()
    {
        // План ровно как в реальном tool-шаге Moonshot: провайдер требует round-trip reasoning для tool-calls.
        var plan = MoonshotToolPlan();
        var store = new ToolCallReasoningStore();
        store.Capture("call_1", "рассуждение перед инструментом");

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            ToolStepRequest(),
            plan,
            store,
            out var patchedJson,
            out var patchKind);

        Assert.True(patched);
        // Вписали reasoning вместо того, чтобы глушить thinking.
        Assert.Equal(LlmChatCompletionRequestPolicy.LlmRequestPatchKind.ReasoningContentReplayed, patchKind);
        var patchedRoot = JsonNode.Parse(patchedJson)!;
        Assert.Equal(
            "рассуждение перед инструментом",
            patchedRoot["messages"]!.AsArray()[1]!["reasoning_content"]!.GetValue<string>());
        // thinking НЕ выключен.
        Assert.Null(patchedRoot["thinking"]);
    }

    [Fact]
    public void Without_captured_reasoning_the_safety_fallback_still_disables_thinking_no_regression()
    {
        var plan = MoonshotToolPlan();

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(
            ToolStepRequest(),
            plan,
            new ToolCallReasoningStore(),
            out var patchedJson,
            out var patchKind);

        Assert.True(patched);
        Assert.Equal(LlmChatCompletionRequestPolicy.LlmRequestPatchKind.AutoToolStepSafetyDisable, patchKind);
        var patchedRoot = JsonNode.Parse(patchedJson)!;
        Assert.Equal("disabled", patchedRoot["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Capture_client_stores_reasoning_from_a_response_keyed_by_tool_call_id()
    {
        var store = new ToolCallReasoningStore();
        var innerResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent("захваченное рассуждение"),
            new FunctionCallContent("call_1", "web_search", new Dictionary<string, object?> { ["q"] = "x" }),
        }));
        var client = new ToolCallReasoningCaptureChatClient(
            new FakeChatClient(innerResponse),
            store,
            NullLogger<ToolCallReasoningCaptureChatClient>.Instance);

        await client.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "поищи") });

        Assert.True(store.TryGet("call_1", out var reasoning));
        Assert.Equal("захваченное рассуждение", reasoning);
    }

    private static LlmExecutionPlan MoonshotToolPlan() =>
        new LlmExecutionPlanner(new LlmProviderCapabilitiesResolver()).CreatePlan(
            new LlmOptions
            {
                Provider = "moonshot",
                BaseUrl = "https://api.moonshot.ai/v1",
                ThinkingMode = LlmThinkingModes.Auto,
            },
            LlmExecutionProfile.ToolEnabled);

    private static string ToolStepRequest() => """
        {
          "model": "kimi-k2.6",
          "messages": [
            { "role": "user", "content": "поищи в вебе" },
            { "role": "assistant", "content": "", "tool_calls": [ { "id": "call_1", "type": "function" } ] },
            { "role": "tool", "content": "{\"results\":[]}", "tool_call_id": "call_1" }
          ]
        }
        """;

    private static JsonObject ParseRequest(string json) => JsonNode.Parse(json)!.AsObject();

    private sealed class FakeChatClient : IChatClient
    {
        private readonly ChatResponse _response;

        public FakeChatClient(ChatResponse response) => _response = response;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(_response);

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
}
