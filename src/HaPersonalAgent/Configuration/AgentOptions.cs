namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: настройки самого agent-приложения, не привязанные к конкретной интеграции.
/// Зачем: state database и workspace должны одинаково задаваться из appsettings, env и Home Assistant add-on UI.
/// Как: класс биндингом заполняется из секции Agent, а значения по умолчанию соответствуют persisted директории /data в add-on и небольшому Telegram context window.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";
    public const string MemoryRetrievalModeBeforeInvoke = "before_invoke";
    public const string MemoryRetrievalModeOnDemandTool = "on_demand_tool";
    public const string CapsuleExtractionModeManual = "manual";
    public const string CapsuleExtractionModeAutoBatched = "auto-batched";

    public string StateDatabasePath { get; set; } = "/data/state.sqlite";

    public string WorkspacePath { get; set; } = "/data/workspace";

    public int WorkspaceMaxMb { get; set; } = 512;

    public int ConversationContextMaxTurns { get; set; } = 12;

    public string MemoryRetrievalMode { get; set; } = MemoryRetrievalModeBeforeInvoke;

    public string CapsuleExtractionMode { get; set; } = CapsuleExtractionModeManual;

    public int CapsuleAutoBatchRawEventThreshold { get; set; } = 20;

    public static string NormalizeMemoryRetrievalMode(string? mode)
    {
        if (string.Equals(mode?.Trim(), MemoryRetrievalModeOnDemandTool, StringComparison.OrdinalIgnoreCase))
        {
            return MemoryRetrievalModeOnDemandTool;
        }

        return MemoryRetrievalModeBeforeInvoke;
    }

    public static bool IsBeforeInvokeRetrieval(string? mode) =>
        string.Equals(
            NormalizeMemoryRetrievalMode(mode),
            MemoryRetrievalModeBeforeInvoke,
            StringComparison.Ordinal);
}
