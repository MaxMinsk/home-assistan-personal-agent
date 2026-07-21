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
    public void Empty_content_on_an_assistant_tool_call_message_becomes_null()
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
        Assert.True(assistant.ContainsKey("content"));
        Assert.Null(assistant["content"]);
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
    public void Already_null_content_is_idempotent()
    {
        var root = ParseRequest("""
            {
              "messages": [ { "role": "assistant", "content": null, "tool_calls": [ { "id": "1" } ] } ]
            }
            """);

        Assert.False(LlmChatCompletionRequestPolicy.TryNormalizeEmptyToolCallContent(root));
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
        Assert.Null(assistant["content"]);
        // Текст пользователя и результат инструмента остаются нетронутыми.
        Assert.Equal("поищи в вебе", JsonNode.Parse(patchedJson)!["messages"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    private static JsonObject ParseRequest(string json) => JsonNode.Parse(json)!.AsObject();
}
