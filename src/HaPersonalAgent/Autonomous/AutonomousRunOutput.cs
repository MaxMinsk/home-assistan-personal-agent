namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: структурированный результат одного фонового запуска агента.
/// Зачем: доставка сводки, уточняющие вопросы и запись в память — разные вещи, и смешивать их в одном куске прозы нельзя.
/// Как: агент возвращает JSON по этому контракту; парсер устойчив к обрамляющему тексту и деградирует в "вся прозa = сводка".
/// </summary>
public sealed record AutonomousRunOutput(
    string Summary,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Questions,
    IReadOnlyList<string> DurableFacts,
    string? NextFocus)
{
    public static AutonomousRunOutput Empty { get; } = new(
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        NextFocus: null);
}
