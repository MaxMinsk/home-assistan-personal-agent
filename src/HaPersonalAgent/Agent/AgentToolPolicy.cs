namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: политика доступа к инструментам в рамках одного run.
/// Зачем: фоновый (автономный) запуск идёт без пользователя — ему нельзя предлагать управление устройствами, а владелец агента
/// вдобавок сам решает галочками, что именно этому агенту разрешено (веб, состояние дома, чтение и запись памяти).
/// Как: нейтральный для домена record в слое Agent (чтобы каталог инструментов не знал про подсистему автономных агентов);
/// передаётся через AgentContext, по умолчанию разрешено всё — обычный интерактивный диалог ничего не теряет.
/// </summary>
public sealed record AgentToolPolicy(
    bool AllowControlActions,
    bool AllowMemoryRead,
    bool AllowMemoryWrite,
    bool AllowWebSearch,
    bool AllowHomeAssistantRead,
    bool AllowScheduledAgentRouting)
{
    /// <summary>Обычный интерактивный диалог: пользователь рядом и может подтвердить действие.</summary>
    public static AgentToolPolicy Default { get; } = new(
        AllowControlActions: true,
        AllowMemoryRead: true,
        AllowMemoryWrite: true,
        AllowWebSearch: true,
        AllowHomeAssistantRead: true,
        AllowScheduledAgentRouting: true);

    /// <summary>
    /// Фоновое исследование: никакого управления устройствами и никаких предложений записи через confirmation
    /// (подтверждать некому). Роутинг контекста в плановых агентов — тоже нет: это capability интерактивного агента,
    /// а фоновому агенту это открыло бы путь к петлям. Остальные оси задаёт владелец агента.
    /// </summary>
    public static AgentToolPolicy ReadOnlyResearch(
        bool allowWebSearch,
        bool allowHomeAssistantRead,
        bool allowMemoryRead) =>
        new(
            AllowControlActions: false,
            AllowMemoryRead: allowMemoryRead,
            AllowMemoryWrite: false,
            AllowWebSearch: allowWebSearch,
            AllowHomeAssistantRead: allowHomeAssistantRead,
            AllowScheduledAgentRouting: false);
}
