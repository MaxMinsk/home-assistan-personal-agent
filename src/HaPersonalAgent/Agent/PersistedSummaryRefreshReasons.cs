namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: нормализованные причины обновления persisted summary.
/// Зачем: единые reason-коды нужны для логов, /status диагностики и стабильного поведения compaction pipeline.
/// Как: содержит константы и helper нормализации входного значения в один из поддерживаемых reason-кодов.
/// </summary>
public static class PersistedSummaryRefreshReasons
{
    public const string None = "none";
    public const string Missing = "missing";
    public const string Threshold = "threshold";
    public const string TopicShift = "topic-shift";
    public const string Manual = "manual";

    public static string Normalize(string? reason) =>
        reason?.Trim().ToLowerInvariant() switch
        {
            Missing => Missing,
            Threshold => Threshold,
            TopicShift => TopicShift,
            Manual => Manual,
            None => None,
            _ => None,
        };
}
