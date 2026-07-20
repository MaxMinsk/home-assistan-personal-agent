namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: границы того, что автономный агент может делать в фоновом запуске.
/// Зачем: фоновый запуск идёт без пользователя рядом, поэтому по решению эпика (HPA-024) он research-only: чтение + дисциплинированная запись в память, без управления устройствами.
/// Как: набор флагов доступа к инструментам плюс жёсткий лимит durable-фактов за запуск; управление Home Assistant сознательно отсутствует и появится только через approve-later (HPA-035).
/// </summary>
public sealed record AutonomousAgentToolScope(
    bool AllowHomeAssistantRead,
    bool AllowWebSearch,
    bool AllowMemoryRead,
    bool AllowMemoryWrite,
    int MaxDurableFactsPerRun)
{
    /// <summary>Максимально допустимое число durable-фактов за один запуск — защита от засорения общей памяти.</summary>
    public const int MaxAllowedDurableFactsPerRun = 5;

    /// <summary>Профиль по умолчанию: полноценное исследование с ограниченной записью в память.</summary>
    public static AutonomousAgentToolScope ResearchDefault { get; } = new(
        AllowHomeAssistantRead: true,
        AllowWebSearch: true,
        AllowMemoryRead: true,
        AllowMemoryWrite: true,
        MaxDurableFactsPerRun: 3);

    public static AutonomousAgentToolScope Create(
        bool allowHomeAssistantRead,
        bool allowWebSearch,
        bool allowMemoryRead,
        bool allowMemoryWrite,
        int maxDurableFactsPerRun) =>
        new(
            allowHomeAssistantRead,
            allowWebSearch,
            allowMemoryRead,
            // Запись без чтения бессмысленна и опасна: агент писал бы, не сверяясь с уже сохранённым.
            allowMemoryWrite && allowMemoryRead,
            Math.Clamp(maxDurableFactsPerRun, 0, MaxAllowedDurableFactsPerRun));
}
