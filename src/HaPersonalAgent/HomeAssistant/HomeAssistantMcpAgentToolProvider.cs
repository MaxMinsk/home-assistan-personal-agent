using System.Net;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: provider MCP tools, которые можно передать в Microsoft Agent Framework.
/// Зачем: агенту нужны Home Assistant tools, но до confirmation policy мы должны отдавать только read-only инструменты.
/// Как: открывает MCP session на время agent run, фильтрует tools через HomeAssistantMcpToolPolicy и возвращает disposable tool set.
/// </summary>
public sealed class HomeAssistantMcpAgentToolProvider : IHomeAssistantMcpAgentToolProvider
{
    private static readonly TimeSpan ToolLoadTimeout = TimeSpan.FromSeconds(5);

    private readonly IHomeAssistantMcpToolConnector _connector;
    private readonly ILogger<HomeAssistantMcpAgentToolProvider> _logger;
    private readonly IOptions<HomeAssistantOptions> _options;
    private readonly HomeAssistantMcpToolPolicy _policy;

    public HomeAssistantMcpAgentToolProvider(
        IOptions<HomeAssistantOptions> options,
        IHomeAssistantMcpToolConnector connector,
        HomeAssistantMcpToolPolicy policy,
        ILogger<HomeAssistantMcpAgentToolProvider> logger)
    {
        _options = options;
        _connector = connector;
        _policy = policy;
        _logger = logger;
    }

    public async Task<HomeAssistantMcpAgentToolSet> CreateReadOnlyToolSetAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        if (!HomeAssistantMcpEndpointBuilder.TryBuild(
                options.Url,
                options.McpEndpoint,
                out var endpoint,
                out var endpointReason)
            || endpoint is null)
        {
            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.InvalidConfiguration,
                endpointReason ?? "Invalid MCP endpoint.");
        }

        if (string.IsNullOrWhiteSpace(options.LongLivedAccessToken))
        {
            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.NotConfigured,
                "HomeAssistant:LongLivedAccessToken is empty.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolLoadTimeout);

        try
        {
            var session = await _connector.ConnectToolsAsync(
                endpoint,
                options.LongLivedAccessToken.Trim(),
                timeout.Token);
            var classifiedTools = session.Tools
                .Select(tool => new
                {
                    Tool = tool,
                    Safety = _policy.Classify(tool.Name, tool.Description),
                })
                .ToArray();
            var exposedTools = classifiedTools
                .Where(tool => tool.Safety == HomeAssistantMcpToolSafety.ReadOnly)
                .Select(tool => tool.Tool)
                .ToArray();
            var confirmationRequiredTools = classifiedTools
                .Where(tool => tool.Safety == HomeAssistantMcpToolSafety.RequiresConfirmation)
                .Select(tool => new HomeAssistantMcpItemInfo(
                    tool.Tool.Name,
                    tool.Tool.Name,
                    tool.Tool.Description))
                .ToArray();

            if (exposedTools.Length == 0)
            {
                await session.DisposeAsync();

                return HomeAssistantMcpAgentToolSet.Available(
                    Array.Empty<Microsoft.Extensions.AI.AIFunction>(),
                    confirmationRequiredTools,
                    totalToolCount: session.Tools.Count,
                    ownsSession: null);
            }

            return HomeAssistantMcpAgentToolSet.Available(
                exposedTools,
                confirmationRequiredTools,
                session.Tools.Count,
                session);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.Unreachable,
                "MCP tool loading timed out.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.AuthFailed,
                "Home Assistant rejected the MCP token.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.IntegrationMissing,
                "Home Assistant MCP Server integration was not found at the configured endpoint.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(
                exception,
                "Home Assistant MCP tool loading failed with HTTP status {StatusCode}",
                exception.StatusCode);

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.Unreachable,
                "Home Assistant MCP endpoint is unreachable.");
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Home Assistant MCP tool loading failed.");

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.Error,
                $"Home Assistant MCP tool loading failed with {exception.GetType().Name}.");
        }
    }
}
