namespace HaPersonalAgent.Configuration;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = "moonshot";

    public string BaseUrl { get; set; } = "https://api.moonshot.ai/v1";

    public string Model { get; set; } = "kimi-k2.5";

    public string ApiKey { get; set; } = string.Empty;
}
