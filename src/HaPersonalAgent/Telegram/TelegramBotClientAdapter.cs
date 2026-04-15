using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: thin adapter поверх библиотеки Telegram.Bot.
/// Зачем: изолируем внешний SDK операциями long polling/update ack/отправки сообщений, чтобы бизнес-логика и тесты не зависели от Telegram.Bot типов.
/// Как: методы делегируют в Telegram.Bot, не логируют token и не занимаются бизнес-логикой команд.
/// </summary>
public sealed class TelegramBotClientAdapter : ITelegramBotClientAdapter
{
    private readonly ITelegramBotClient _client;

    public TelegramBotClientAdapter(string botToken)
        : this(new TelegramBotClient(botToken))
    {
    }

    internal TelegramBotClientAdapter(ITelegramBotClient client)
    {
        _client = client;
    }

    public async Task DeleteWebhookAsync(bool dropPendingUpdates, CancellationToken cancellationToken) =>
        await _client.DeleteWebhook(
            dropPendingUpdates: dropPendingUpdates,
            cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<Update>> GetUpdatesAsync(
        int? offset,
        int limit,
        int timeoutSeconds,
        IReadOnlyList<UpdateType> allowedUpdates,
        CancellationToken cancellationToken) =>
        await _client.GetUpdates(
            offset: offset,
            limit: limit,
            timeout: timeoutSeconds,
            allowedUpdates: allowedUpdates,
            cancellationToken: cancellationToken);

    public async Task SendTypingAsync(long chatId, CancellationToken cancellationToken) =>
        await _client.SendChatAction(
            chatId: chatId,
            action: ChatAction.Typing,
            cancellationToken: cancellationToken);

    public async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken) =>
        _ = await SendMessageWithIdAsync(chatId, text, cancellationToken);

    public async Task<int> SendMessageWithIdAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken)
    {
        var message = await _client.SendMessage(
            chatId: chatId,
            text: text,
            cancellationToken: cancellationToken);

        return message.Id;
    }

    public async Task EditMessageTextAsync(
        long chatId,
        int messageId,
        string text,
        CancellationToken cancellationToken) =>
        await _client.EditMessageText(
            chatId: chatId,
            messageId: messageId,
            text: text,
            cancellationToken: cancellationToken);

    public async Task DeleteMessageAsync(
        long chatId,
        int messageId,
        CancellationToken cancellationToken) =>
        await _client.DeleteMessage(
            chatId: chatId,
            messageId: messageId,
            cancellationToken: cancellationToken);

    public async Task SetCommandsAsync(
        IReadOnlyList<(string Command, string Description)> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var botCommands = commands
            .Where(command =>
                !string.IsNullOrWhiteSpace(command.Command)
                && !string.IsNullOrWhiteSpace(command.Description))
            .Select(command => new BotCommand
            {
                Command = command.Command,
                Description = command.Description,
            })
            .ToArray();
        await _client.SetMyCommands(
            commands: botCommands,
            cancellationToken: cancellationToken);
    }

    public async Task SendConfirmationMessageAsync(
        long chatId,
        string text,
        string confirmationId,
        CancellationToken cancellationToken)
    {
        var replyMarkup = new InlineKeyboardMarkup(
            new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "Подтвердить",
                        $"confirm:approve:{confirmationId}"),
                    InlineKeyboardButton.WithCallbackData(
                        "Отклонить",
                        $"confirm:reject:{confirmationId}"),
                },
            });
        await _client.SendMessage(
            chatId: chatId,
            text: text,
            replyMarkup: replyMarkup,
            cancellationToken: cancellationToken);
    }

    public async Task ClearInlineKeyboardAsync(
        long chatId,
        int messageId,
        CancellationToken cancellationToken) =>
        await _client.EditMessageReplyMarkup(
            chatId: chatId,
            messageId: messageId,
            replyMarkup: null,
            cancellationToken: cancellationToken);

    public async Task AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text,
        bool showAlert,
        CancellationToken cancellationToken) =>
        await _client.AnswerCallbackQuery(
            callbackQueryId: callbackQueryId,
            text: text,
            showAlert: showAlert,
            cancellationToken: cancellationToken);
}
