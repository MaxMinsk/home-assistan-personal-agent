namespace HaPersonalAgent.Configuration;

public sealed class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    public string Url { get; set; } = "http://supervisor/core";

    public string LongLivedAccessToken { get; set; } = string.Empty;

    public string McpEndpoint { get; set; } = "/api/mcp";
}
