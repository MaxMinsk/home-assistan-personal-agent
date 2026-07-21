using System.Text.Json.Nodes;
using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: регрессионные тесты на HTTP 400 "Invalid request: text content is empty" от Moonshot (0.12.0, field report).
/// Зачем: любой tool-шаг падал — assistant-сообщение с tool_calls уходило с content: "", что провайдер отвергает;
/// по спецификации OpenAI у такого сообщения content допустим как null. Веб-поиск лишь вскрыл давний баг, сделав tool-round-trip частым.
/// Как: проверяем нормализацию на сыром JSON запроса и через полный patch-пайплайн с реальным планом Moonshot.
/// </summary>
public class LlmEmptyToolCallContentTests
{
    [Fact]
    public void Empty_content_on_an_assistant_tool_call_message_is_removed()
    {
        var root = ParseRequest("""
            {
              "messages": [
                { "role": "user", "content": "поищи в вебе" },
                { "role": "assistant", "content": "", "tool_calls": [ { "id": "1", "type": "function" } ] },
                { "role": "tool", "content": "результат поиска", "tool_call_id": "1" }
              ]
            }
            """);

        var changed = LlmChatCompletionRequestPolicy.TryNormalizeEmptyToolCallContent(root);

        Assert.True(changed);
        var assistant = root["messages"]!.AsArray()[1]!.AsObject();
        // Поле полностью удалено — не пустая строка и не null (Moonshot отвергает оба).
        Assert.False(assistant.ContainsKey("content"));
        Assert.NotNull(assistant["tool_calls"]);
    }

    [Fact]
    public void Assistant_tool_call_message_with_real_text_is_left_alone()
    {
        var root = ParseRequest("""
            {
              "messages": [
                { "role": "assistant", "content": "сейчас поищу", "tool_calls": [ { "id": "1" } ] }
              ]
            }
            """);

        Assert.False(LlmChatCompletionRequestPolicy.TryNormalizeEmptyToolCallContent(root));
        Assert.Equal("сейчас поищу", root["messages"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Assistant_message_without_tool_calls_is_not_touched()
    {
        // Пустой ответ без tool_calls — это симптом другой проблемы, и молча превращать его
        // в null нельзя: пусть провайдер честно ругается, а мы увидим это в логе.
        var root = ParseRequest("""
            {
              "messages": [ { "role": "assistant", "content": "" } ]
            }
            """);

        Assert.False(LlmChatCompletionRequestPolicy.TryNormalizeEmptyToolCallContent(root));
    }

    [Fact]
    public void Null_content_on_a_tool_call_message_is_also_removed()
    {
        // Moonshot отвергает и null, и "" — убираем оба.
        var root = ParseRequest("""
            {
              "messages": [ { "role": "assistant", "content": null, "tool_calls": [ { "id": "1" } ] } ]
            }
            """);

        Assert.True(LlmChatCompletionRequestPolicy.TryNormalizeEmptyToolCallContent(root));
        Assert.False(root["messages"]!.AsArray()[0]!.AsObject().ContainsKey("content"));
    }

    [Fact]
    public void Missing_content_is_left_alone_and_diagnostic_describes_states_without_leaking_text()
    {
        var root = ParseRequest("""
            {
              "messages": [
                { "role": "system", "content": "инструкции" },
                { "role": "assistant", "tool_calls": [ { "id": "1" } ] },
                { "role": "tool", "content": "результат" }
              ]
            }
            """);

        // Ключа content нет — трогать нечего.
        Assert.False(LlmChatCompletionRequestPolicy.TryNormalizeEmptyToolCallContent(root));

        var states = LlmChatCompletionRequestPolicy.DescribeMessageContent(root);
        Assert.Contains("assistant+tools:no-content", states, StringComparison.Ordinal);
        Assert.Contains("str[", states, StringComparison.Ordinal);
        // Диагностика не раскрывает сам текст сообщений.
        Assert.DoesNotContain("инструкции", states, StringComparison.Ordinal);
        Assert.DoesNotContain("результат", states, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_patch_pipeline_repairs_the_request_that_moonshot_rejected()
    {
        // План ровно как в упавшем запуске: moonshot, thinking auto, профиль с инструментами.
        var plan = new LlmExecutionPlanner(new LlmProviderCapabilitiesResolver()).CreatePlan(
            new LlmOptions
            {
                Provider = "moonshot",
                BaseUrl = "https://api.moonshot.ai/v1",
                ThinkingMode = LlmThinkingModes.Auto,
            },
            LlmExecutionProfile.ToolEnabled);

        const string request = """
            {
              "model": "kimi-k2.6",
              "messages": [
                { "role": "user", "content": "поищи в вебе" },
                { "role": "assistant", "content": "", "tool_calls": [ { "id": "call_1", "type": "function" } ] },
                { "role": "tool", "content": "{\"results\":[]}", "tool_call_id": "call_1" }
              ]
            }
            """;

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(request, plan, out var patchedJson);

        Assert.True(patched);
        var assistant = JsonNode.Parse(patchedJson)!["messages"]!.AsArray()[1]!.AsObject();
        Assert.False(assistant.ContainsKey("content"));
        // Текст пользователя и результат инструмента остаются нетронутыми.
        Assert.Equal("поищи в вебе", JsonNode.Parse(patchedJson)!["messages"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Empty_content_is_normalized_even_when_the_thinking_patch_is_disabled()
    {
        // Регрессия из живого лога (correlation 3f03abe0): DeepReasoning с provider-default имеет
        // ShouldPatchChatCompletionRequest == false, из-за чего нормализация раньше не запускалась и Moonshot валил tool-шаг 400.
        var plan = new LlmExecutionPlan(
            LlmExecutionProfile.DeepReasoning,
            new LlmProviderCapabilities(
                ProviderKey: "moonshot",
                SupportsTools: true,
                SupportsStreaming: true,
                SupportsReasoning: true,
                RequiresReasoningContentRoundTripForToolCalls: true,
                SupportsReasoningContentRoundTrip: true,
                SupportsExplicitThinkingEnable: false,
                ThinkingControlStyle: LlmThinkingControlStyle.OpenAiCompatibleThinkingObject),
            RequestedThinkingMode: LlmThinkingModes.Enabled,
            EffectiveThinkingMode: LlmEffectiveThinkingMode.ProviderDefault,
            Reason: "test");

        // Предусловие: план НЕ патчит thinking — именно тот путь, что падал.
        Assert.False(plan.ShouldPatchChatCompletionRequest);

        const string request = """
            {
              "model": "kimi-k2.6",
              "messages": [
                { "role": "assistant", "content": "", "tool_calls": [ { "id": "call_1", "type": "function" } ] },
                { "role": "tool", "content": "{\"results\":[]}", "tool_call_id": "call_1" }
              ]
            }
            """;

        var patched = LlmChatCompletionRequestPolicy.TryPatchRequestJson(request, plan, out var patchedJson);

        Assert.True(patched);
        Assert.False(JsonNode.Parse(patchedJson)!["messages"]!.AsArray()[0]!.AsObject().ContainsKey("content"));
    }

    private static JsonObject ParseRequest(string json) => JsonNode.Parse(json)!.AsObject();
}
