namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: настройки подключения к Home Assistant Core и MCP endpoint.
/// Зачем: агенту нужен единый источник URL, long-lived token и пути MCP для будущих HA tools.
/// Как: секция HomeAssistant биндится из defaults, add-on options и env overrides; секрет хранится только как значение опции.
/// </summary>
public sealed class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    public string Url { get; set; } = "http://supervisor/core";

    public string LongLivedAccessToken { get; set; } = string.Empty;

    public string McpEndpoint { get; set; } = "/api/mcp";
}
