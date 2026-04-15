using System.Net;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: health/discovery слой для Home Assistant MCP Server.
/// Зачем: add-on должен стартовать даже без токена или при недоступном `/api/mcp`, но уметь показать понятный MCP status.
/// Как: валидирует options, лениво вызывает connector с коротким timeout и мапит HTTP/SDK ошибки в безопасный DiscoveryResult.
/// </summary>
public sealed class HomeAssistantMcpClient : IHomeAssistantMcpClient
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    private readonly IHomeAssistantAuthTokenProvider _authTokenProvider;
    private readonly IHomeAssistantMcpConnector _connector;
    private readonly ILogger<HomeAssistantMcpClient> _logger;
    private readonly IOptions<HomeAssistantOptions> _options;

    public HomeAssistantMcpClient(
        IOptions<HomeAssistantOptions> options,
        IHomeAssistantMcpConnector connector,
        IHomeAssistantAuthTokenProvider authTokenProvider,
        ILogger<HomeAssistantMcpClient> logger)
    {
        _options = options;
        _connector = connector;
        _authTokenProvider = authTokenProvider;
        _logger = logger;
    }

    public async Task<HomeAssistantMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        if (!HomeAssistantMcpEndpointBuilder.TryBuild(
                options.Url,
                options.McpEndpoint,
                out var endpoint,
                out var endpointReason)
            || endpoint is null)
        {
            _logger.LogWarning(
                "Home Assistant MCP discovery skipped because endpoint configuration is invalid: {Reason}",
                endpointReason);

            return HomeAssistantMcpDiscoveryResult.InvalidConfiguration(endpointReason ?? "Invalid MCP endpoint.");
        }

        var authToken = _authTokenProvider.Resolve(endpoint);
        if (!authToken.IsConfigured || string.IsNullOrWhiteSpace(authToken.Value))
        {
            _logger.LogInformation(
                "Home Assistant MCP discovery skipped for {Endpoint}: {Reason}",
                endpoint,
                authToken.Reason);

            return HomeAssistantMcpDiscoveryResult.NotConfigured(
                endpoint,
                authToken.Reason ?? "Home Assistant auth token is missing.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);

        try
        {
            _logger.LogInformation(
                "Home Assistant MCP discovery starting for {Endpoint} using auth source {AuthSource}.",
                endpoint,
                authToken.Source);

            var discovery = await _connector.DiscoverAsync(
                endpoint,
                authToken.Value,
                timeout.Token);

            _logger.LogInformation(
                "Home Assistant MCP discovery reachable at {Endpoint}: {ToolCount} tools, {PromptCount} prompts.",
                endpoint,
                discovery.Tools.Count,
                discovery.Prompts.Count);

            return HomeAssistantMcpDiscoveryResult.Reachable(endpoint, discovery);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Home Assistant MCP discovery timed out after {TimeoutSeconds}s for {Endpoint}.",
                DiscoveryTimeout.TotalSeconds,
                endpoint);

            return HomeAssistantMcpDiscoveryResult.Failed(
                HomeAssistantMcpStatus.Unreachable,
                endpoint,
                "MCP discovery timed out.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(
                exception,
                "Home Assistant MCP discovery auth failed for {Endpoint} using auth source {AuthSource}.",
                endpoint,
                authToken.Source);

            return HomeAssistantMcpDiscoveryResult.Failed(
                HomeAssistantMcpStatus.AuthFailed,
                endpoint,
                "Home Assistant rejected the MCP token.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                exception,
                "Home Assistant MCP Server integration was not found at {Endpoint}.",
                endpoint);

            return HomeAssistantMcpDiscoveryResult.Failed(
                HomeAssistantMcpStatus.IntegrationMissing,
                endpoint,
                "Home Assistant MCP Server integration was not found at the configured endpoint.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "Home Assistant MCP discovery failed with HTTP status {StatusCode}",
                exception.StatusCode);

            return HomeAssistantMcpDiscoveryResult.Failed(
                HomeAssistantMcpStatus.Unreachable,
                endpoint,
                "Home Assistant MCP endpoint is unreachable.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Home Assistant MCP discovery failed.");

            return HomeAssistantMcpDiscoveryResult.Failed(
                HomeAssistantMcpStatus.Error,
                endpoint,
                $"Home Assistant MCP discovery failed with {exception.GetType().Name}.");
        }
    }
}
