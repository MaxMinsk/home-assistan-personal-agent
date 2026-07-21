using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: координатор доставки результатов тика планировщика (HPA-039, часть B).
/// Зачем: решение «один бриф vs сводный дайджест» и сохранение reply-якорей не должны жить ни в раннере (он больше
/// не доставляет фоновые запуски), ни в планировщике (его дело — «когда»). Здесь — единственное место этой логики.
/// Как: 0 результатов — ничего; 1 — обычный бриф; ≥2 — заземлённый поиск связей + один дайджест; затем якоря в запуски.
/// </summary>
public sealed class AutonomousDigestDelivery
{
    private readonly IAutonomousAgentRepository _repository;
    private readonly ILogger<AutonomousDigestDelivery> _logger;
    private readonly IAutonomousAgentNotifier? _notifier;
    private readonly IAutonomousConnectionFinder? _connectionFinder;

    public AutonomousDigestDelivery(
        IAutonomousAgentRepository repository,
        ILogger<AutonomousDigestDelivery> logger,
        IAutonomousAgentNotifier? notifier = null,
        IAutonomousConnectionFinder? connectionFinder = null)
    {
        _repository = repository;
        _logger = logger;
        _notifier = notifier;
        _connectionFinder = connectionFinder;
    }

    public async Task DeliverAsync(
        IReadOnlyList<AutonomousRunDelivery> deliveries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        if (_notifier is null || deliveries.Count == 0)
        {
            return;
        }

        if (deliveries.Count == 1)
        {
            var single = deliveries[0];
            var messageId = await _notifier.DeliverAsync(
                single.Definition,
                single.Run,
                single.Output,
                single.ProposedActions,
                cancellationToken);
            await PersistAnchorAsync(single.Run, messageId, cancellationToken);
            return;
        }

        var connections = _connectionFinder is not null
            ? await _connectionFinder.FindConnectionsAsync(deliveries, cancellationToken)
            : Array.Empty<string>();

        _logger.LogInformation(
            "Delivering a consolidated digest for {AgentCount} autonomous agents with {ConnectionCount} cross-agent connection(s).",
            deliveries.Count,
            connections.Count);

        var anchors = await _notifier.DeliverDigestAsync(deliveries, connections, cancellationToken);
        foreach (var anchor in anchors)
        {
            var delivery = deliveries.FirstOrDefault(item => item.Run.Id == anchor.RunId);
            if (delivery is not null)
            {
                await PersistAnchorAsync(delivery.Run, anchor.DeliveredMessageId, cancellationToken);
            }
        }
    }

    private async Task PersistAnchorAsync(
        AutonomousAgentRun run,
        string? deliveredMessageId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deliveredMessageId))
        {
            return;
        }

        await _repository.UpdateRunAsync(run with { DeliveredMessageId = deliveredMessageId }, cancellationToken);
    }
}
