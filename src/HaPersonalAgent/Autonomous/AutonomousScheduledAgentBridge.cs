using System.Globalization;
using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: реализация моста между conversation-агентом и подсистемой плановых агентов (HPA-043/044).
/// Зачем: держит зависимость в правильную сторону — Agent зависит от нейтрального порта, а этот адаптер (в слое Autonomous) знает о деталях.
/// Как: тонкая обёртка над AutonomousAgentService — список агентов, роутинг заметки в inbox (источник Conversation), и текущая сводка агента.
/// </summary>
public sealed class AutonomousScheduledAgentBridge : IScheduledAgentBridge
{
    private const int RecentRunsToScan = 8;

    private readonly AutonomousAgentService _service;

    public AutonomousScheduledAgentBridge(AutonomousAgentService service)
    {
        _service = service;
    }

    public async Task<IReadOnlyList<ScheduledAgentInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var definitions = await _service.ListAsync(cancellationToken);
        return definitions
            .Select(definition => new ScheduledAgentInfo(
                definition.Id,
                definition.Name,
                definition.Mission,
                definition.Status.ToString(),
                ToIso(definition.NextRunUtc)))
            .ToList();
    }

    public async Task<bool> RouteNoteAsync(string agentId, string note, CancellationToken cancellationToken)
    {
        var entry = await _service.RecordReplyAsync(
            agentId,
            note,
            AutonomousAgentReplySource.Conversation,
            runId: null,
            cancellationToken);
        return entry is not null;
    }

    public async Task<ScheduledAgentBriefing?> GetBriefingAsync(string agentId, CancellationToken cancellationToken)
    {
        var definition = await _service.GetAsync(agentId, cancellationToken);
        if (definition is null)
        {
            return null;
        }

        var runs = await _service.ListRunsAsync(agentId, RecentRunsToScan, cancellationToken);
        var lastCompleted = runs.FirstOrDefault(run =>
            run.Status == AutonomousAgentRunStatus.Completed && !string.IsNullOrWhiteSpace(run.Summary));
        var continuity = await _service.GetContinuityAsync(agentId, cancellationToken);

        var openQuestions = string.IsNullOrWhiteSpace(continuity?.OpenQuestions)
            ? Array.Empty<string>()
            : continuity!.OpenQuestions!
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ScheduledAgentBriefing(
            definition.Id,
            definition.Name,
            HasRun: lastCompleted is not null || definition.LastRunUtc is not null,
            LastSummary: lastCompleted?.Summary,
            OpenQuestions: openQuestions,
            Focus: continuity?.Focus,
            NextRunUtc: ToIso(definition.NextRunUtc),
            LastRunUtc: ToIso(definition.LastRunUtc));
    }

    private static string? ToIso(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);
}
