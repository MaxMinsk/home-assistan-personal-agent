namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: фабрика Telegram Bot API adapter.
/// Зачем: gateway должен создавать client только когда Telegram token реально настроен, а тесты должны заменять создание клиента.
/// Как: Create принимает token из безопасно загруженных options и возвращает adapter поверх Telegram.Bot.
/// </summary>
public interface ITelegramBotClientAdapterFactory
{
    ITelegramBotClientAdapter Create(string botToken);
}
