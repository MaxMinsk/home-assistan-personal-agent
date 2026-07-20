namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: настройки подсистемы автономных агентов (планировщик и бюджеты запуска).
/// Зачем: фоновые запуски идут без пользователя, поэтому их частота, параллельность и предельная длительность должны быть управляемыми и безопасными по умолчанию.
/// Как: биндится из add-on options так же, как остальные секции; Enabled — рубильник всей подсистемы.
/// </summary>
public sealed class AutonomousAgentOptions
{
    public const string SectionName = "AutonomousAgents";

    public const string CatchUpRunOnce = "run_once";
    public const string CatchUpSkip = "skip";

    /// <summary>Рубильник подсистемы: при false планировщик не запускается вовсе.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Предельная длительность одного фонового запуска — защита от зависшего агента.</summary>
    public int RunTimeoutMinutes { get; set; } = 10;

    /// <summary>Сколько запусков разных агентов может идти одновременно.</summary>
    public int MaxConcurrentRuns { get; set; } = 1;

    /// <summary>
    /// Потолок вызовов инструментов за один фоновый запуск. Защищает от длинного дорогого цикла
    /// (особенно с включённым веб-поиском), когда рядом нет пользователя, чтобы прервать агента.
    /// </summary>
    public int MaxToolCallsPerRun { get; set; } = 20;

    /// <summary>
    /// Что делать с пропущенными запусками (add-on был выключен): run_once — выполнить один раз сейчас; skip — просто перейти к следующему сроку.
    /// </summary>
    public string CatchUpPolicy { get; set; } = CatchUpRunOnce;

    public static bool IsSkipCatchUp(string? policy) =>
        string.Equals(policy?.Trim(), CatchUpSkip, StringComparison.OrdinalIgnoreCase);
}
