namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: политика доступа к рискованным инструментам в рамках одного run.
/// Зачем: фоновый (автономный) запуск идёт без пользователя рядом, поэтому ему нельзя предлагать управление устройствами и свободную запись в память — но обычный диалог этого лишать нельзя.
/// Как: нейтральный для домена record в слое Agent (чтобы каталог инструментов не знал про подсистему автономных агентов); передаётся через AgentContext, по умолчанию всё разрешено.
/// </summary>
public sealed record AgentToolPolicy(
    bool AllowControlActions,
    bool AllowMemoryWrite)
{
    /// <summary>Обычный интерактивный диалог: пользователь рядом и может подтвердить действие.</summary>
    public static AgentToolPolicy Default { get; } = new(
        AllowControlActions: true,
        AllowMemoryWrite: true);

    /// <summary>Фоновое исследование: только чтение, никакого управления и предложений записи через confirmation.</summary>
    public static AgentToolPolicy ReadOnlyResearch { get; } = new(
        AllowControlActions: false,
        AllowMemoryWrite: false);
}
