using System.Net;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Memory;

/// <summary>
/// What: application-facing Memory MCP client — health/discovery probe + generic tool call.
/// Why: HPA-003 needs the app to reach Memory MCP over streamable HTTP and log a startup health check;
/// the add-on must start fine even when Memory MCP is unconfigured or unreachable.
/// How: validates the configured endpoint/token, applies a short timeout, maps SDK/HTTP errors to a safe
/// status for discovery, and delegates the actual SDK work to <see cref="IMemoryMcpConnector"/>.
/// </summary>
public sealed class MemoryMcpClient : IMemoryMcpClient
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    private readonly IMemoryMcpConnector _connector;
    private readonly IOptions<MemoryMcpOptions> _options;
    private readonly ILogger<MemoryMcpClient> _logger;

    public MemoryMcpClient(
        IOptions<MemoryMcpOptions> options,
        IMemoryMcpConnector connector,
        ILogger<MemoryMcpClient> logger)
    {
        _options = options;
        _connector = connector;
        _logger = logger;
    }

    public async Task<MemoryMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        if (!MemoryMcpEndpointBuilder.TryBuild(options.Endpoint, out var endpoint, out var endpointReason)
            || endpoint is null)
        {
            // An empty endpoint is the expected "disabled" state, not a misconfiguration.
            return string.IsNullOrWhiteSpace(options.Endpoint)
                ? MemoryMcpDiscoveryResult.NotConfigured(string.Empty, endpointReason ?? "Memory MCP endpoint is empty.")
                : MemoryMcpDiscoveryResult.InvalidConfiguration(options.Endpoint, endpointReason ?? "Invalid Memory MCP endpoint.");
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            return MemoryMcpDiscoveryResult.NotConfigured(endpoint.ToString(), "MemoryMcp:Token is empty.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);

        try
        {
            _logger.LogInformation("Memory MCP health check starting for {Endpoint}.", endpoint);

            var connection = await _connector.DiscoverAsync(endpoint, options.Token, timeout.Token);

            _logger.LogInformation(
                "Memory MCP reachable at {Endpoint}: server version {ServerVersion}, {ToolCount} tools.",
                endpoint,
                connection.ServerVersion ?? "unknown",
                connection.ToolCount);

            return MemoryMcpDiscoveryResult.Reachable(endpoint.ToString(), connection.ServerVersion, connection.ToolCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Memory MCP health check timed out after {TimeoutSeconds}s for {Endpoint}.",
                OperationTimeout.TotalSeconds,
                endpoint);

            return MemoryMcpDiscoveryResult.Failed(MemoryMcpStatus.Unreachable, endpoint.ToString(), "Memory MCP health check timed out.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(exception, "Memory MCP rejected the bearer token for {Endpoint}.", endpoint);

            return MemoryMcpDiscoveryResult.Failed(MemoryMcpStatus.AuthFailed, endpoint.ToString(), "Memory MCP rejected the bearer token.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Memory MCP is unreachable at {Endpoint} (HTTP {StatusCode}).", endpoint, exception.StatusCode);

            return MemoryMcpDiscoveryResult.Failed(MemoryMcpStatus.Unreachable, endpoint.ToString(), "Memory MCP endpoint is unreachable.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Memory MCP health check failed for {Endpoint}.", endpoint);

            return MemoryMcpDiscoveryResult.Failed(MemoryMcpStatus.Error, endpoint.ToString(), $"Memory MCP health check failed with {exception.GetType().Name}.");
        }
    }

    public async Task<MemoryMcpToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var options = _options.Value;

        if (!MemoryMcpEndpointBuilder.TryBuild(options.Endpoint, out var endpoint, out var endpointReason)
            || endpoint is null)
        {
            throw new InvalidOperationException($"Memory MCP endpoint is not configured: {endpointReason}");
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("Memory MCP token is not configured.");
        }

        // Diagnostic logging: surface the exact tool call (endpoint, token fingerprint, verbatim
        // arguments) and the raw server response in the add-on log, so a recall that returns an
        // empty/unexpected result can be diagnosed from logs instead of the agent's own narration.
        var tokenFingerprint = options.Token.Length >= 4 ? options.Token[^4..] : "----";
        _logger.LogInformation(
            "Memory MCP call {Tool} -> {Endpoint} (token …{TokenTail}); args: {Args}",
            toolName,
            endpoint,
            tokenFingerprint,
            DescribeArguments(arguments));

        var result = await _connector.CallToolAsync(endpoint, options.Token, toolName, arguments, cancellationToken);

        _logger.LogInformation(
            "Memory MCP call {Tool} result: isError={IsError}, text[{Length}]: {Preview}",
            toolName,
            result.IsError,
            result.Text?.Length ?? 0,
            Preview(result.Text, 240));

        return result;
    }

    private static string DescribeArguments(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "(none)";
        }

        return string.Join(", ", arguments.Select(pair => $"{pair.Key}={DescribeValue(pair.Value)}"));
    }

    private static string DescribeValue(object? value) => value switch
    {
        null => "null",
        string text => text,
        IEnumerable<string> items => $"[{string.Join("|", items)}]",
        _ => value.ToString() ?? "null",
    };

    private static string Preview(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(empty)";
        }

        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "…";
    }
}
