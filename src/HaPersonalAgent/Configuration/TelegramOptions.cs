namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: настройки Telegram bot интеграции.
/// Зачем: будущий Telegram gateway должен знать bot token и allowlist пользователей, которым разрешено общаться с агентом.
/// Как: token хранится строкой, allowlist биндится в массив long, а UX-параметры задают preview reasoning при длинных ответах.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public long[] AllowedUserIds { get; set; } = Array.Empty<long>();

    public bool ReasoningPreviewEnabled { get; set; }

    public int ReasoningPreviewDelaySeconds { get; set; } = 7;
}
