using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: thin adapter поверх библиотеки Telegram.Bot.
/// Зачем: ограничиваем внешний SDK тремя операциями MVP: long polling, снятие webhook и отправка текста.
/// Как: методы вызывают extension methods Telegram.Bot, не логируют token и не занимаются бизнес-логикой команд.
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
        await _client.SendMessage(
            chatId: chatId,
            text: text,
            cancellationToken: cancellationToken);
}
