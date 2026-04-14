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

    private readonly IHomeAssistantMcpConnector _connector;
    private readonly ILogger<HomeAssistantMcpClient> _logger;
    private readonly IOptions<HomeAssistantOptions> _options;

    public HomeAssistantMcpClient(
        IOptions<HomeAssistantOptions> options,
        IHomeAssistantMcpConnector connector,
        ILogger<HomeAssistantMcpClient> logger)
    {
        _options = options;
        _connector = connector;
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
            return HomeAssistantMcpDiscoveryResult.InvalidConfiguration(endpointReason ?? "Invalid MCP endpoint.");
        }

        if (string.IsNullOrWhiteSpace(options.LongLivedAccessToken))
        {
            return HomeAssistantMcpDiscoveryResult.NotConfigured(
                endpoint,
                "HomeAssistant:LongLivedAccessToken is empty.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);

        try
        {
            var discovery = await _connector.DiscoverAsync(
                endpoint,
                options.LongLivedAccessToken.Trim(),
                timeout.Token);

            return HomeAssistantMcpDiscoveryResult.Reachable(endpoint, discovery);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeAssistantMcpDiscoveryResult.Failed(
                HomeAssistantMcpStatus.Unreachable,
                endpoint,
                "MCP discovery timed out.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return HomeAssistantMcpDiscoveryResult.Failed(
                HomeAssistantMcpStatus.AuthFailed,
                endpoint,
                "Home Assistant rejected the MCP token.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            return HomeAssistantMcpDiscoveryResult.Failed(
                HomeAssistantMcpStatus.IntegrationMissing,
                endpoint,
                "Home Assistant MCP Server integration was not found at the configured endpoint.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(
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
            _logger.LogDebug(exception, "Home Assistant MCP discovery failed.");

            return HomeAssistantMcpDiscoveryResult.Failed(
                HomeAssistantMcpStatus.Error,
                endpoint,
                $"Home Assistant MCP discovery failed with {exception.GetType().Name}.");
        }
    }
}
