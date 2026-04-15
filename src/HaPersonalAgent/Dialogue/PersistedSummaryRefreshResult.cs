using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: результат принудительного refresh persisted summary.
/// Зачем: Telegram/Web adapters должны получить явный outcome refresh операции без чтения внутренних деталей DialogueService.
/// Как: содержит флаги конфигурации/обновления, user-facing сообщение и актуальный summary snapshot (если доступен).
/// </summary>
public sealed record PersistedSummaryRefreshResult(
    bool IsConfigured,
    bool IsUpdated,
    string Message,
    ConversationSummaryMemory? Summary = null);
