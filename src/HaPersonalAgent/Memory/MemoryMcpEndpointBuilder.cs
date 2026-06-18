namespace HaPersonalAgent.Memory;

/// <summary>
/// What: validates the configured Memory MCP endpoint into an absolute http/https URI.
/// Why: unlike Home Assistant (base URL + relative path), Memory MCP is configured as a single absolute
/// endpoint (e.g. the local add-on http://&lt;host&gt;:8099/mcp or the public https://memory.kazmin.tech/mcp).
/// How: requires an absolute http/https URL; returns a clear reason otherwise.
/// </summary>
public static class MemoryMcpEndpointBuilder
{
    public static bool TryBuild(string endpoint, out Uri? uri, out string? reason)
    {
        uri = null;
        reason = null;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            reason = "MemoryMcp:Endpoint is empty.";
            return false;
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var parsed))
        {
            reason = "MemoryMcp:Endpoint must be an absolute URL.";
            return false;
        }

        if (!IsHttpEndpoint(parsed))
        {
            reason = "MemoryMcp:Endpoint must use http or https.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsHttpEndpoint(Uri endpoint) =>
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
