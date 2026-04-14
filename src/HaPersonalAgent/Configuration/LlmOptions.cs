namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: настройки LLM backend для Microsoft Agent Framework.
/// Зачем: проект должен уметь стартовать с Moonshot/Kimi как OpenAI-compatible provider и позже переключаться на другой backend.
/// Как: provider/base URL/model/API key приходят из конфигурации, а defaults настроены на Moonshot без Azure-зависимостей.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = "moonshot";

    public string BaseUrl { get; set; } = "https://api.moonshot.ai/v1";

    public string Model { get; set; } = "kimi-k2.5";

    public string ApiKey { get; set; } = string.Empty;
}
