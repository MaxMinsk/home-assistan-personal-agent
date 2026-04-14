namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: настройки Telegram bot интеграции.
/// Зачем: будущий Telegram gateway должен знать bot token и allowlist пользователей, которым разрешено общаться с агентом.
/// Как: token хранится строкой, а allowlist биндится в массив long из appsettings или Home Assistant add-on options.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public long[] AllowedUserIds { get; set; } = Array.Empty<long>();
}
