namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: настройки LLM backend для Microsoft Agent Framework.
/// Зачем: проект должен уметь стартовать с Moonshot/Kimi как OpenAI-compatible provider и позже переключаться на другой backend без переписывания runtime.
/// Как: provider/base URL/model/API key/thinking mode приходят из конфигурации, а defaults настроены на Moonshot без Azure-зависимостей.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = "moonshot";

    public string BaseUrl { get; set; } = "https://api.moonshot.ai/v1";

    public string Model { get; set; } = "kimi-k2.6";

    public string ApiKey { get; set; } = string.Empty;

    public string ThinkingMode { get; set; } = LlmThinkingModes.Auto;

    /// <summary>
    /// HPA-041 follow-up: вписывать ли захваченный reasoning_content обратно в исходящий tool-шаг,
    /// чтобы модель продолжала думать во время работы с инструментами. По умолчанию OFF — безопасное
    /// поведение (thinking глушится на continuation-шаге). Включать осознанно: у части провайдеров эхо
    /// reasoning_content может дать HTTP 400, поэтому это опт-ин с проверкой на живом ключе.
    /// </summary>
    public bool ReplayReasoningContentToWire { get; set; }

    public string RouterMode { get; set; } = LlmRouterModes.Enforced;

    /// <summary>Модель для простых/дешёвых запросов (thinking off). По умолчанию — недорогая k2.6.</summary>
    public string RouterSmallModel { get; set; } = "kimi-k2.6";

    /// <summary>
    /// Модель для глубоких/сложных задач (deep-намерение). Позволяет ставить дорогой reasoning-флагман (напр. kimi-k3)
    /// ТОЛЬКО на deep, оставляя основной/маленькой моделью что-то дешевле. Пусто => используется основная Model.
    /// </summary>
    public string RouterDeepModel { get; set; } = "kimi-k3";

    public int RouterMaxInputCharsForSmall { get; set; } = 1_800;

    public int RouterMaxHistoryMessagesForSmall { get; set; } = 10;

    public int RouterSimpleMaxInputChars { get; set; } = 6_000;

    public int RouterSimpleMaxHistoryMessages { get; set; } = 6;

    public bool RouterSimpleAllowTools { get; set; } = false;

    public string RouterDeepKeywords { get; set; } = "пошагово,step-by-step,deep reasoning";
}
