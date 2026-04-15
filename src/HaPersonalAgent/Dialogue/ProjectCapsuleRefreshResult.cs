namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: результат ручного или batched refresh проектных капсул.
/// Зачем: Telegram/Web adapters должны получить явный outcome extraction без знания внутренних шагов парсинга/хранилища.
/// Как: возвращает флаги configured/updated, user-facing сообщение, число сохраненных капсул и id последнего обработанного raw event.
/// </summary>
public sealed record ProjectCapsuleRefreshResult(
    bool IsConfigured,
    bool IsUpdated,
    string Message,
    int CapsuleCount = 0,
    long LastProcessedRawEventId = 0);
