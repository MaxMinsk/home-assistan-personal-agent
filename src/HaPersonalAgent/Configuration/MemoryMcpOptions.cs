namespace HaPersonalAgent.Configuration;

/// <summary>
/// What: typed options for the Memory MCP integration (a second MCP server alongside Home Assistant).
/// Why: HPA-003 wires a Memory MCP client (streamable HTTP) for durable memory; this binds the add-on
/// config keys (memory_mcp_*) so endpoint/token/scope and the store selector are configurable.
/// How: bound from the "MemoryMcp" configuration section; the token is a secret and is never logged.
/// </summary>
public sealed class MemoryMcpOptions
{
    public const string SectionName = "MemoryMcp";

    public const string StoreTypeSqlite = "sqlite";
    public const string StoreTypeMemoryMcp = "memory_mcp";

    public const string DefaultDomain = "development";
    public const string DefaultProject = "ha-personal-agent";

    /// <summary>Absolute streamable-HTTP endpoint, e.g. https://memory.kazmin.tech/mcp. Empty = disabled.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Bearer token for the Memory MCP server. Empty = not configured.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Memory MCP domain namespace for this project's notes.</summary>
    public string Domain { get; set; } = DefaultDomain;

    /// <summary>Memory MCP project (sub-axis) for this project's notes.</summary>
    public string Project { get; set; } = DefaultProject;

    /// <summary>Durable-memory backend selector: "sqlite" (default) or "memory_mcp" (wired in HPA-004).</summary>
    public string StoreType { get; set; } = StoreTypeSqlite;

    /// <summary>True when both an endpoint and a token are present, so the client can attempt a connection.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Token);
}
