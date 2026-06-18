namespace HaPersonalAgent.Memory;

/// <summary>
/// What: the SDK-facing connector for Memory MCP — opens a streamable-HTTP session and performs one operation.
/// Why: isolates ModelContextProtocol SDK details so <see cref="MemoryMcpClient"/> (validation, timeout,
/// error mapping) stays testable with a fake connector.
/// How: each call opens a short-lived session (connect, act, dispose), mirroring the Home Assistant connector.
/// </summary>
public interface IMemoryMcpConnector
{
    Task<MemoryMcpConnection> DiscoverAsync(
        Uri endpoint,
        string token,
        CancellationToken cancellationToken);

    Task<MemoryMcpToolResult> CallToolAsync(
        Uri endpoint,
        string token,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken);
}

/// <summary>Connection metadata read at discovery time: the server build version and the tool count.</summary>
public sealed record MemoryMcpConnection(string? ServerVersion, int ToolCount);
