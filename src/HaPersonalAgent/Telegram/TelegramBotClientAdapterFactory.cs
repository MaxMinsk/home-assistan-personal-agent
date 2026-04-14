namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: production-фабрика Telegram Bot API adapter.
/// Зачем: DI должен знать, как создать настоящий Telegram client, но остальной код не должен зависеть от конструктора TelegramBotClient.
/// Как: при каждом запуске gateway создает TelegramBotClientAdapter с актуальным bot token из Home Assistant add-on options.
/// </summary>
public sealed class TelegramBotClientAdapterFactory : ITelegramBotClientAdapterFactory
{
    public ITelegramBotClientAdapter Create(string botToken) =>
        new TelegramBotClientAdapter(botToken);
}
