using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types.Enums;

namespace HaPersonalAgent.Telegram;

/// <summary>
/// Что: background service для Telegram long polling.
/// Зачем: Home Assistant add-on должен принимать сообщения без входящего webhook и без открытых портов.
/// Как: при наличии bot token создает Telegram client, читает offset из SQLite, получает updates через getUpdates и сохраняет следующий offset после обработки.
/// </summary>
public sealed class TelegramBotGateway : BackgroundService
{
    private static readonly IReadOnlyList<UpdateType> AllowedUpdates =
    [
        UpdateType.Message,
        UpdateType.CallbackQuery,
    ];
    private static readonly IReadOnlyList<(string Command, string Description)> BotCommands =
    [
        ("start", "Справка по командам"),
        ("status", "Статус агента и памяти"),
        ("resetcontext", "Очистить контекст текущего чата"),
        ("showsummary", "Показать persisted summary"),
        ("refreshsummary", "Принудительно пересобрать summary"),
        ("showrawevents", "Показать последние raw events"),
        ("showcapsules", "Показать project capsules"),
        ("refreshcapsules", "Обновить project capsules"),
        ("think", "Deep reasoning без tools"),
        ("approve", "Подтвердить действие по id"),
        ("reject", "Отклонить действие по id"),
    ];

    private readonly ITelegramBotClientAdapterFactory _clientFactory;
    private readonly ILogger<TelegramBotGateway> _logger;
    private readonly AgentStateRepository _stateRepository;
    private readonly TelegramUpdateHandler _updateHandler;
    private readonly IOptions<TelegramOptions> _telegramOptions;

    public TelegramBotGateway(
        ITelegramBotClientAdapterFactory clientFactory,
        IOptions<TelegramOptions> telegramOptions,
        AgentStateRepository stateRepository,
        TelegramUpdateHandler updateHandler,
        ILogger<TelegramBotGateway> logger)
    {
        _clientFactory = clientFactory;
        _telegramOptions = telegramOptions;
        _stateRepository = stateRepository;
        _updateHandler = updateHandler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _telegramOptions.Value;
        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            _logger.LogInformation("Telegram gateway is disabled because Telegram:BotToken is not configured.");
            return;
        }

        if (options.AllowedUserIds.Length == 0)
        {
            _logger.LogWarning("Telegram gateway is enabled, but Telegram:AllowedUserIds is empty; all users will be ignored.");
        }

        var client = _clientFactory.Create(options.BotToken);
        await RunPollingLoopAsync(client, options, stoppingToken);
    }

    private async Task RunPollingLoopAsync(
        ITelegramBotClientAdapter client,
        TelegramOptions options,
        CancellationToken stoppingToken)
    {
        var offset = await _stateRepository.GetTelegramUpdateOffsetAsync(stoppingToken);
        var webhookDeleted = false;
        var commandsConfigured = false;
        var iteration = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            iteration++;
            try
            {
                if (!webhookDeleted)
                {
                    await client.DeleteWebhookAsync(dropPendingUpdates: false, stoppingToken);
                    webhookDeleted = true;
                    _logger.LogInformation("Telegram long polling started from offset {TelegramUpdateOffset}", offset);
                }

                if (!commandsConfigured)
                {
                    commandsConfigured = await TryConfigureBotCommandsAsync(client, stoppingToken);
                }

                var updates = await client.GetUpdatesAsync(
                    ToTelegramOffset(offset),
                    limit: 20,
                    timeoutSeconds: 30,
                    AllowedUpdates,
                    stoppingToken);

                if (updates.Count > 0)
                {
                    _logger.LogInformation(
                        "Telegram polling iteration {Iteration} received {UpdateCount} updates at offset {Offset}.",
                        iteration,
                        updates.Count,
                        offset);
                }

                foreach (var update in updates)
                {
                    _logger.LogInformation(
                        "Telegram polling is processing update {TelegramUpdateId}.",
                        update.Id);
                    await _updateHandler.HandleAsync(client, update, options, stoppingToken);

                    offset = update.Id + 1L;
                    await _stateRepository.SaveTelegramUpdateOffsetAsync(offset.Value, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Telegram long polling iteration failed; retrying after delay.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> TryConfigureBotCommandsAsync(
        ITelegramBotClientAdapter client,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.SetCommandsAsync(BotCommands, cancellationToken);
            _logger.LogInformation(
                "Telegram bot commands configured: {CommandCount}.",
                BotCommands.Count);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to configure Telegram bot commands; command suggestions in Telegram UI may be missing.");
            return false;
        }
    }

    private static int? ToTelegramOffset(long? offset)
    {
        if (offset is null)
        {
            return null;
        }

        return offset > int.MaxValue
            ? int.MaxValue
            : (int)offset.Value;
    }
}
