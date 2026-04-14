namespace HaPersonalAgent.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string StateDatabasePath { get; set; } = "/data/state.sqlite";

    public string WorkspacePath { get; set; } = "/data/workspace";

    public int WorkspaceMaxMb { get; set; } = 512;
}
