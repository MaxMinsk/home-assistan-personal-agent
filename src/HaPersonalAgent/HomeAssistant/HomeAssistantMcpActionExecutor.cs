using System.Text.Json;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: executor подтвержденных Home Assistant MCP actions.
/// Зачем: generic confirmation layer должен уметь запускать Home Assistant как один из доменов risky actions.
/// Как: принимает PendingConfirmation с kind home_assistant_mcp, открывает короткую MCP session, находит operation name как tool и вызывает InvokeAsync с JSON payload.
/// </summary>
public sealed class HomeAssistantMcpActionExecutor : IConfirmationActionExecutor
{
    public const string HomeAssistantMcpActionKind = "home_assistant_mcp";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHomeAssistantAuthTokenProvider _authTokenProvider;
    private readonly IHomeAssistantMcpToolConnector _connector;
    private readonly ILogger<HomeAssistantMcpActionExecutor> _logger;
    private readonly IOptions<HomeAssistantOptions> _options;
    private readonly HomeAssistantMcpToolPolicy _policy;

    public HomeAssistantMcpActionExecutor(
        IOptions<HomeAssistantOptions> options,
        IHomeAssistantMcpToolConnector connector,
        IHomeAssistantAuthTokenProvider authTokenProvider,
        HomeAssistantMcpToolPolicy policy,
        ILogger<HomeAssistantMcpActionExecutor> logger)
    {
        _options = options;
        _connector = connector;
        _authTokenProvider = authTokenProvider;
        _policy = policy;
        _logger = logger;
    }

    public string ActionKind => HomeAssistantMcpActionKind;

    public async Task<ConfirmationActionExecutionResult> ExecuteAsync(
        PendingConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        if (!string.Equals(confirmation.ActionKind, HomeAssistantMcpActionKind, StringComparison.Ordinal))
        {
            return ConfirmationActionExecutionResult.Failure($"Unsupported action kind '{confirmation.ActionKind}'.");
        }

        var options = _options.Value;
        if (!HomeAssistantMcpEndpointBuilder.TryBuild(
                options.Url,
                options.McpEndpoint,
                out var endpoint,
                out var endpointReason)
            || endpoint is null)
        {
            return ConfirmationActionExecutionResult.Failure(endpointReason ?? "Invalid Home Assistant MCP endpoint.");
        }

        var authToken = _authTokenProvider.Resolve(endpoint);
        if (!authToken.IsConfigured || string.IsNullOrWhiteSpace(authToken.Value))
        {
            return ConfirmationActionExecutionResult.Failure(
                authToken.Reason ?? "Home Assistant auth token is missing.");
        }

        try
        {
            _logger.LogInformation(
                "Executing approved Home Assistant MCP confirmation {ConfirmationId} with tool {ToolName} using auth source {AuthSource}.",
                confirmation.Id,
                confirmation.OperationName,
                authToken.Source);

            await using var session = await _connector.ConnectToolsAsync(
                endpoint,
                authToken.Value,
                cancellationToken);
            var tool = session.Tools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, confirmation.OperationName, StringComparison.Ordinal));

            if (tool is null)
            {
                return ConfirmationActionExecutionResult.Failure($"MCP tool '{confirmation.OperationName}' was not found.");
            }

            if (_policy.Classify(tool.Name, tool.Description) == HomeAssistantMcpToolSafety.ReadOnly)
            {
                return ConfirmationActionExecutionResult.Failure($"MCP tool '{confirmation.OperationName}' is read-only and should not be approved as a control action.");
            }

            var arguments = ParseArguments(confirmation.PayloadJson);
            var result = await tool.InvokeAsync(new AIFunctionArguments(arguments), cancellationToken);
            var resultJson = JsonSerializer.Serialize(result, JsonOptions);

            _logger.LogInformation(
                "Home Assistant MCP confirmation {ConfirmationId} completed with tool {ToolName}.",
                confirmation.Id,
                confirmation.OperationName);

            return ConfirmationActionExecutionResult.Success(resultJson);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Home Assistant confirmation {ConfirmationId} has invalid JSON payload.",
                confirmation.Id);

            return ConfirmationActionExecutionResult.Failure("Action payload is not a valid JSON object.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Home Assistant MCP confirmation {ConfirmationId} failed.",
                confirmation.Id);

            return ConfirmationActionExecutionResult.Failure($"MCP execution failed with {exception.GetType().Name}.");
        }
    }

    private static Dictionary<string, object?> ParseArguments(string argumentsJson)
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            argumentsJson,
            JsonOptions);

        return arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }
}
