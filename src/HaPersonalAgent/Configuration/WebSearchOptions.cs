namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: настройки веб-поиска для агента.
/// Зачем: автономным исследовательским агентам нужен внешний источник фактов, а провайдер и ключ задаются владельцем в опциях add-on.
/// Как: провайдер выбирается строкой (сейчас поддержан brave), ключ хранится как password-опция; без ключа инструмент просто не регистрируется.
/// </summary>
public sealed class WebSearchOptions
{
    public const string SectionName = "WebSearch";

    public const string ProviderBrave = "brave";

    public string Provider { get; set; } = ProviderBrave;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Сколько результатов запрашивать по умолчанию; Brave принимает максимум 20.</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>Код страны для регионального ранжирования (например BY, RU); пусто — без привязки.</summary>
    public string Country { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public static bool IsBrave(string? provider) =>
        string.IsNullOrWhiteSpace(provider)
        || string.Equals(provider.Trim(), ProviderBrave, StringComparison.OrdinalIgnoreCase);
}
