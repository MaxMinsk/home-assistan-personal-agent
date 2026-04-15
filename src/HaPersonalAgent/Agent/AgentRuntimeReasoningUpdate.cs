namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: единичное обновление reasoning-текста во время streaming run.
/// Зачем: transport-адаптеры (например Telegram) могут показывать пользователю промежуточный прогресс без ожидания финального ответа.
/// Как: runtime эмитит короткие text-delta фрагменты с correlation id текущего run.
/// </summary>
public sealed record AgentRuntimeReasoningUpdate(
    string CorrelationId,
    string TextDelta);
