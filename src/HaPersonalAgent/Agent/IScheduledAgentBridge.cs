namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: краткая карточка планового агента для conversation-агента.
/// Зачем: чтобы решить, какому агенту релевантна реплика из чата, нужно знать его имя и миссию.
/// Как: нейтральная проекция определения планового агента (без деталей подсистемы Autonomous).
/// </summary>
public sealed record ScheduledAgentInfo(
    string Id,
    string Name,
    string Mission,
    string Status,
    string? NextRunUtc);

/// <summary>
/// Что: текущее состояние планового агента для ответа в чате.
/// Зачем: обратное направление моста — пользователь спрашивает «что нашёл агент?», и conversation-агент отвечает из реального состояния.
/// Как: последняя сводка + открытые вопросы + фокус + время следующего запуска; HasRun различает «ещё ни разу не запускался».
/// </summary>
public sealed record ScheduledAgentBriefing(
    string Id,
    string Name,
    bool HasRun,
    string? LastSummary,
    IReadOnlyList<string> OpenQuestions,
    string? Focus,
    string? NextRunUtc,
    string? LastRunUtc);

/// <summary>
/// Что: нейтральный порт между интерактивным conversation-агентом и подсистемой плановых агентов.
/// Зачем: каталог инструментов (слой Agent) должен уметь читать плановых агентов и класть им контекст, НЕ завися от Autonomous напрямую
/// (Autonomous уже зависит от Agent — прямая обратная зависимость размыла бы слои).
/// Как: реализуется адаптером в слое Autonomous; в каталоге инструментов используется как optional-зависимость (null-safe, когда подсистемы нет).
/// </summary>
public interface IScheduledAgentBridge
{
    Task<IReadOnlyList<ScheduledAgentInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Кладёт короткую заметку в очередь агента; она попадёт в контекст его следующего планового запуска. false — агента нет.</summary>
    Task<bool> RouteNoteAsync(string agentId, string note, CancellationToken cancellationToken);

    Task<ScheduledAgentBriefing?> GetBriefingAsync(string agentId, CancellationToken cancellationToken);
}
