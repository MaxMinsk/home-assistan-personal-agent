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

    Task SendTypingAsync(long chatId, CancellationToken cancellationToken);

    Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken);

    Task<int> SendMessageWithIdAsync(long chatId, string text, CancellationToken cancellationToken);

    Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken cancellationToken);

    Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken);

    Task SetCommandsAsync(
        IReadOnlyList<(string Command, string Description)> commands,
        CancellationToken cancellationToken);

    Task SendConfirmationMessageAsync(
        long chatId,
        string text,
        string confirmationId,
        CancellationToken cancellationToken);

    Task ClearInlineKeyboardAsync(
        long chatId,
        int messageId,
        CancellationToken cancellationToken);

    Task AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text,
        bool showAlert,
        CancellationToken cancellationToken);
}
