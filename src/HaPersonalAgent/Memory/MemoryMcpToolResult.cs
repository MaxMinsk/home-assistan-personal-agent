namespace HaPersonalAgent.Memory;

/// <summary>
/// What: a transport-agnostic result of a Memory MCP tool call.
/// Why: callers (e.g. HPA-004 durable memory) need the textual + structured payload without depending
/// on the ModelContextProtocol SDK types directly.
/// How: produced by the connector from the SDK CallToolResult (text content joined; structured content as JSON).
/// </summary>
public sealed record MemoryMcpToolResult(bool IsError, string Text, string? StructuredJson);
