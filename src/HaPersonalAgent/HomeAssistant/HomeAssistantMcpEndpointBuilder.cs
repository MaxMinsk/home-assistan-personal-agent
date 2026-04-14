namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: helper для сборки абсолютного URL Home Assistant MCP endpoint.
/// Зачем: add-on options дают `ha_url` и `mcp_endpoint` отдельно, а MCP SDK ожидает абсолютный HTTP/HTTPS URI.
/// Как: валидирует базовый URL, поддерживает относительный `/api/mcp` и аккуратно сохраняет path вроде `/core`.
/// </summary>
public static class HomeAssistantMcpEndpointBuilder
{
    public static bool TryBuild(
        string homeAssistantUrl,
        string mcpEndpoint,
        out Uri? endpoint,
        out string? reason)
    {
        endpoint = null;
        reason = null;

        if (string.IsNullOrWhiteSpace(mcpEndpoint))
        {
            reason = "HomeAssistant:McpEndpoint is empty.";
            return false;
        }

        var trimmedEndpoint = mcpEndpoint.Trim();
        if (trimmedEndpoint.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(trimmedEndpoint, UriKind.Absolute, out var absoluteEndpoint))
        {
            if (!IsHttpEndpoint(absoluteEndpoint))
            {
                reason = "HomeAssistant:McpEndpoint must use http or https.";
                return false;
            }

            endpoint = absoluteEndpoint;
            return true;
        }

        if (string.IsNullOrWhiteSpace(homeAssistantUrl))
        {
            reason = "HomeAssistant:Url is empty.";
            return false;
        }

        var normalizedBaseUrl = homeAssistantUrl.Trim();
        if (!normalizedBaseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            normalizedBaseUrl += "/";
        }

        if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var baseUri) || !IsHttpEndpoint(baseUri))
        {
            reason = "HomeAssistant:Url must be an absolute http or https URL.";
            return false;
        }

        endpoint = new Uri(baseUri, trimmedEndpoint.TrimStart('/'));
        return true;
    }

    private static bool IsHttpEndpoint(Uri endpoint) =>
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
