using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: минимальный порт для Telegram Bot API, который нужен нашему gateway.
/// Зачем: бизнес-логика обработки updates должна тестироваться без реального Telegram token и сетевых вызовов.
/// Как: production adapter делегирует в Telegram.Bot, а тесты подставляют in-memory fake с теми же методами.
/// </summary>
public interface ITelegramBotClientAdapter
{
    Task DeleteWebhookAsync(bool dropPendingUpdates, CancellationToken cancellationToken);

    Task<IReadOnlyList<Update>> GetUpdatesAsync(
        int? offset,
        int limit,
        int timeoutSeconds,
        IReadOnlyList<UpdateType> allowedUpdates,
        CancellationToken cancellationToken);

    Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken);
}
