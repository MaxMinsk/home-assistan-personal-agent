namespace HaPersonalAgent.Memory;

/// <summary>
/// What: a safe, token-free snapshot of a Memory MCP health/discovery probe.
/// Why: used for the startup health-check log and diagnostics; never carries the bearer token.
/// How: factory methods build the result for each <see cref="MemoryMcpStatus"/> case.
/// </summary>
public sealed record MemoryMcpDiscoveryResult(
    MemoryMcpStatus Status,
    string EndpointUrl,
    bool TokenConfigured,
    string? ServerVersion,
    int ToolCount,
    string? Reason)
{
    public static MemoryMcpDiscoveryResult NotConfigured(string endpointUrl, string reason) =>
        new(MemoryMcpStatus.NotConfigured, endpointUrl, false, null, 0, reason);

    public static MemoryMcpDiscoveryResult InvalidConfiguration(string endpointUrl, string reason) =>
        new(MemoryMcpStatus.InvalidConfiguration, endpointUrl, false, null, 0, reason);

    public static MemoryMcpDiscoveryResult Reachable(string endpointUrl, string? serverVersion, int toolCount) =>
        new(MemoryMcpStatus.Reachable, endpointUrl, true, serverVersion, toolCount, null);

    public static MemoryMcpDiscoveryResult Failed(MemoryMcpStatus status, string endpointUrl, string reason) =>
        new(status, endpointUrl, true, null, 0, reason);
}
