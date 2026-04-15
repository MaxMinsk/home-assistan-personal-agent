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

    private readonly IHomeAssistantAuthTokenProvider _authTokenProvider;
    private readonly IHomeAssistantMcpToolConnector _connector;
    private readonly ILogger<HomeAssistantMcpAgentToolProvider> _logger;
    private readonly IOptions<HomeAssistantOptions> _options;
    private readonly HomeAssistantMcpToolPolicy _policy;

    public HomeAssistantMcpAgentToolProvider(
        IOptions<HomeAssistantOptions> options,
        IHomeAssistantMcpToolConnector connector,
        IHomeAssistantAuthTokenProvider authTokenProvider,
        HomeAssistantMcpToolPolicy policy,
        ILogger<HomeAssistantMcpAgentToolProvider> logger)
    {
        _options = options;
        _connector = connector;
        _authTokenProvider = authTokenProvider;
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
            _logger.LogWarning(
                "Home Assistant MCP tool loading skipped because endpoint configuration is invalid: {Reason}",
                endpointReason);

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.InvalidConfiguration,
                endpointReason ?? "Invalid MCP endpoint.");
        }

        var authToken = _authTokenProvider.Resolve(endpoint);
        if (!authToken.IsConfigured || string.IsNullOrWhiteSpace(authToken.Value))
        {
            _logger.LogInformation(
                "Home Assistant MCP tool loading skipped for {Endpoint}: {Reason}",
                endpoint,
                authToken.Reason);

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.NotConfigured,
                authToken.Reason ?? "Home Assistant auth token is missing.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolLoadTimeout);

        try
        {
            _logger.LogInformation(
                "Home Assistant MCP tool loading starting for {Endpoint} using auth source {AuthSource}.",
                endpoint,
                authToken.Source);

            var session = await _connector.ConnectToolsAsync(
                endpoint,
                authToken.Value,
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

                _logger.LogInformation(
                    "Home Assistant MCP tools loaded: total {TotalToolCount}, read-only exposed {ReadOnlyToolCount}, confirmation-required {ConfirmationToolCount}.",
                    session.Tools.Count,
                    0,
                    confirmationRequiredTools.Length);

                return HomeAssistantMcpAgentToolSet.Available(
                    Array.Empty<Microsoft.Extensions.AI.AIFunction>(),
                    confirmationRequiredTools,
                    totalToolCount: session.Tools.Count,
                    ownsSession: null);
            }

            _logger.LogInformation(
                "Home Assistant MCP tools loaded: total {TotalToolCount}, read-only exposed {ReadOnlyToolCount}, confirmation-required {ConfirmationToolCount}.",
                session.Tools.Count,
                exposedTools.Length,
                confirmationRequiredTools.Length);

            return HomeAssistantMcpAgentToolSet.Available(
                exposedTools,
                confirmationRequiredTools,
                session.Tools.Count,
                session);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Home Assistant MCP tool loading timed out after {TimeoutSeconds}s.",
                ToolLoadTimeout.TotalSeconds);

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.Unreachable,
                "MCP tool loading timed out.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(
                exception,
                "Home Assistant MCP tool loading auth failed for {Endpoint} using auth source {AuthSource}.",
                endpoint,
                authToken.Source);

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.AuthFailed,
                "Home Assistant rejected the MCP token.");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                exception,
                "Home Assistant MCP Server integration was not found at {Endpoint}.",
                endpoint);

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.IntegrationMissing,
                "Home Assistant MCP Server integration was not found at the configured endpoint.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "Home Assistant MCP tool loading failed with HTTP status {StatusCode}",
                exception.StatusCode);

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.Unreachable,
                "Home Assistant MCP endpoint is unreachable.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Home Assistant MCP tool loading failed.");

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.Error,
                $"Home Assistant MCP tool loading failed with {exception.GetType().Name}.");
        }
    }
}
