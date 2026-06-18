namespace HaPersonalAgent.Memory;

/// <summary>
/// What: the outcome of a Memory MCP health/discovery probe.
/// Why: the add-on must start even when Memory MCP is unconfigured or unreachable, while still
/// surfacing a clear status in logs / a future /status command.
/// How: mirrors the Home Assistant MCP status enum; mapped from endpoint/token validation and SDK/HTTP errors.
/// </summary>
public enum MemoryMcpStatus
{
    NotConfigured,
    InvalidConfiguration,
    Reachable,
    AuthFailed,
    Unreachable,
    Error,
}
