namespace HaPersonalAgent.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public long[] AllowedUserIds { get; set; } = Array.Empty<long>();
}
