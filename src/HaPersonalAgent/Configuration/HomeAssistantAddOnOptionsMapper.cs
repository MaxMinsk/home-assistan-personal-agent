using System.Text.Json;

namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: преобразователь /data/options.json из Home Assistant add-on UI в обычные .NET configuration keys.
/// Зачем: HA использует snake_case options, а приложение внутри работает с typed options и секциями Agent/Telegram/Llm/HomeAssistant.
/// Как: scalar поля мапятся по таблице, а Telegram allowlist дополнительно принимает массив, строку или одиночное число для совместимости UI.
/// </summary>
public static class HomeAssistantAddOnOptionsMapper
{
    private static readonly char[] TelegramUserIdSeparators = [',', ';', ' ', '\n', '\r', '\t'];

    private static readonly IReadOnlyDictionary<string, string> ScalarMappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["telegram_bot_token"] = $"{TelegramOptions.SectionName}:BotToken",
            ["reasoning_preview_enabled"] = $"{TelegramOptions.SectionName}:ReasoningPreviewEnabled",
            ["reasoning_preview_delay_seconds"] = $"{TelegramOptions.SectionName}:ReasoningPreviewDelaySeconds",
            ["ha_url"] = $"{HomeAssistantOptions.SectionName}:Url",
            ["ha_long_lived_access_token"] = $"{HomeAssistantOptions.SectionName}:LongLivedAccessToken",
            ["mcp_endpoint"] = $"{HomeAssistantOptions.SectionName}:McpEndpoint",
            ["llm_provider"] = $"{LlmOptions.SectionName}:Provider",
            ["llm_base_url"] = $"{LlmOptions.SectionName}:BaseUrl",
            ["llm_model"] = $"{LlmOptions.SectionName}:Model",
            ["llm_api_key"] = $"{LlmOptions.SectionName}:ApiKey",
            ["llm_thinking_mode"] = $"{LlmOptions.SectionName}:ThinkingMode",
            ["llm_router_mode"] = $"{LlmOptions.SectionName}:RouterMode",
            ["llm_router_small_model"] = $"{LlmOptions.SectionName}:RouterSmallModel",
            ["llm_router_max_input_chars_for_small"] = $"{LlmOptions.SectionName}:RouterMaxInputCharsForSmall",
            ["llm_router_max_history_messages_for_small"] = $"{LlmOptions.SectionName}:RouterMaxHistoryMessagesForSmall",
            ["llm_router_simple_max_input_chars"] = $"{LlmOptions.SectionName}:RouterSimpleMaxInputChars",
            ["llm_router_simple_max_history_messages"] = $"{LlmOptions.SectionName}:RouterSimpleMaxHistoryMessages",
            ["llm_router_simple_allow_tools"] = $"{LlmOptions.SectionName}:RouterSimpleAllowTools",
            ["llm_router_deep_keywords"] = $"{LlmOptions.SectionName}:RouterDeepKeywords",
            ["state_database_path"] = $"{AgentOptions.SectionName}:StateDatabasePath",
            ["workspace_path"] = $"{AgentOptions.SectionName}:WorkspacePath",
            ["workspace_max_mb"] = $"{AgentOptions.SectionName}:WorkspaceMaxMb",
            ["memory_retrieval_mode"] = $"{AgentOptions.SectionName}:MemoryRetrievalMode",
            ["capsule_extraction_mode"] = $"{AgentOptions.SectionName}:CapsuleExtractionMode",
            ["capsule_auto_batch_raw_event_threshold"] = $"{AgentOptions.SectionName}:CapsuleAutoBatchRawEventThreshold",
        };

    public static IReadOnlyDictionary<string, string?> MapFileIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string?>();
        }

        return MapJson(File.ReadAllText(path));
    }

    public static IReadOnlyDictionary<string, string?> MapJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return MapRoot(document.RootElement);
    }

    private static IReadOnlyDictionary<string, string?> MapRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Home Assistant add-on options must be a JSON object.");
        }

        var mapped = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in ScalarMappings)
        {
            if (root.TryGetProperty(mapping.Key, out var sourceValue))
            {
                var value = ConvertScalar(sourceValue);
                if (value is not null)
                {
                    mapped[mapping.Value] = value;
                }
            }
        }

        AddTelegramUserIds(root, mapped);

        return mapped;
    }

    private static void AddTelegramUserIds(JsonElement root, IDictionary<string, string?> mapped)
    {
        const string sourceKey = "allowed_telegram_user_ids";
        const string targetKey = $"{TelegramOptions.SectionName}:AllowedUserIds";

        if (!root.TryGetProperty(sourceKey, out var sourceValue) ||
            sourceValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        var index = 0;
        foreach (var value in EnumerateTelegramUserIds(sourceValue))
        {
            mapped[$"{targetKey}:{index}"] = value;
            index++;
        }
    }

    private static IEnumerable<string> EnumerateTelegramUserIds(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var userId in EnumerateTelegramUserIds(item))
                {
                    yield return userId;
                }
            }

            yield break;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var rawValue = value.GetString();
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                yield break;
            }

            foreach (var token in rawValue.Split(
                         TelegramUserIdSeparators,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return token;
            }

            yield break;
        }

        var scalarValue = ConvertScalar(value);
        if (scalarValue is not null)
        {
            yield return scalarValue;
        }
    }

    private static string? ConvertScalar(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
}
