namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: результат фактического выполнения подтвержденного risky action.
/// Зачем: generic ConfirmationService должен отделять approve/reject orchestration от конкретного executor: Home Assistant, files или workflows.
/// Как: executor возвращает success flag, JSON результата или безопасное описание ошибки.
/// </summary>
public sealed record ConfirmationActionExecutionResult(
    bool IsSuccess,
    string? ResultJson,
    string? Error)
{
    public static ConfirmationActionExecutionResult Success(string resultJson) =>
        new(IsSuccess: true, resultJson, Error: null);

    public static ConfirmationActionExecutionResult Failure(string error) =>
        new(IsSuccess: false, ResultJson: null, error);
}
