namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: response generic confirmation service для transport adapter.
/// Зачем: Telegram handler не должен знать детали SQLite, Home Assistant MCP, file tools или других risky executors.
/// Как: содержит high-level outcome, флаг успеха и готовое сообщение для отправки пользователю.
/// </summary>
public sealed record ConfirmationDecisionResult(
    ConfirmationDecisionOutcome Outcome,
    bool IsSuccess,
    string Message,
    string? ConfirmationId = null,
    string? ResultJson = null);
