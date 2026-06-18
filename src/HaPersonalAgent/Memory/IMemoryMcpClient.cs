namespace HaPersonalAgent.Memory;

/// <summary>
/// What: the application-facing Memory MCP client — a health/discovery probe and a generic tool call.
/// Why: HPA-003 lets the app reach Memory MCP tools (status / notes_search / notes_upsert / memory_context)
/// over streamable HTTP and log a startup health check; HPA-004 will build durable memory on top.
/// How: validates the configured endpoint/token, applies a short timeout, and delegates to the connector.
/// </summary>
public interface IMemoryMcpClient
{
    Task<MemoryMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);

    Task<MemoryMcpToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken);
}
