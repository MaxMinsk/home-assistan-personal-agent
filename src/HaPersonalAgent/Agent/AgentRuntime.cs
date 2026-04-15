using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.HomeAssistant;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: первая реализация runtime поверх Microsoft Agent Framework.
/// Зачем: нужен учебный vertical slice, который создает MAF ChatClientAgent, подключает OpenAI-compatible LLM и дает агенту безопасные tools.
/// Как: на каждый run выбирает LLM execution plan, собирает tools по профилю и запускает ChatClientAgent без привязки к Telegram.
/// </summary>
public sealed class AgentRuntime : IAgentRuntime
{
    private const string Instructions =
        """
        You are Home Assistant Personal Agent, a learning-first assistant built to explore Microsoft Agent Framework.
        Keep answers concise and practical.
        Use the status tool when the user asks about application status, version, uptime, configuration mode, or health.
        Use the home_assistant_mcp_status tool when the user asks whether MCP is available, why Home Assistant access fails, or which Home Assistant MCP tools are visible.
        Use Home Assistant MCP tools only for read-only questions about current home state, history, or diagnostics.
        For Home Assistant requests that change state, call propose_home_assistant_mcp_action when it is available.
        Never claim a Home Assistant control action was executed until the app reports completion after user approval.
        Never reveal secrets or raw tokens.
        """;

    private readonly IConfirmationService? _confirmationService;
    private readonly IHomeAssistantMcpAgentToolProvider? _homeAssistantMcpToolProvider;
    private readonly HomeAssistantMcpStatusTool? _homeAssistantMcpStatusTool;
    private readonly LlmExecutionPlanner _executionPlanner;
    private readonly ILogger<AgentRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<LlmOptions> _llmOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly AgentStatusTool _statusTool;

    public AgentRuntime(
        IOptions<LlmOptions> llmOptions,
        AgentStatusTool statusTool,
        LlmExecutionPlanner executionPlanner,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IHomeAssistantMcpAgentToolProvider? homeAssistantMcpToolProvider = null,
        IConfirmationService? confirmationService = null,
        HomeAssistantMcpStatusTool? homeAssistantMcpStatusTool = null)
    {
        _llmOptions = llmOptions;
        _statusTool = statusTool;
        _executionPlanner = executionPlanner;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentRuntime>();
        _serviceProvider = serviceProvider;
        _homeAssistantMcpToolProvider = homeAssistantMcpToolProvider;
        _confirmationService = confirmationService;
        _homeAssistantMcpStatusTool = homeAssistantMcpStatusTool;
    }

    public AgentRuntimeHealth GetHealth()
    {
        var options = _llmOptions.Value;

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return AgentRuntimeHealth.NotConfigured(options, "Llm:ApiKey is missing.");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
        {
            return AgentRuntimeHealth.NotConfigured(options, "Llm:BaseUrl is not a valid absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            return AgentRuntimeHealth.NotConfigured(options, "Llm:Model is missing.");
        }

        if (!LlmThinkingModes.IsValid(options.ThinkingMode))
        {
            return AgentRuntimeHealth.NotConfigured(options, "Llm:ThinkingMode must be one of: auto, disabled, enabled.");
        }

        return AgentRuntimeHealth.Configured(options);
    }

    public async Task<AgentRuntimeResponse> SendAsync(
        string message,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(context);

        var health = GetHealth();
        if (!health.IsConfigured)
        {
            return new AgentRuntimeResponse(
                context.CorrelationId,
                IsConfigured: false,
                $"Agent runtime is not configured: {health.Reason}",
                health);
        }

        var executionPlan = _executionPlanner.CreatePlan(_llmOptions.Value, context.ExecutionProfile);
        await using var homeAssistantMcpTools = await CreateHomeAssistantMcpToolsAsync(
            executionPlan,
            cancellationToken);
        _logger.LogInformation(
            "Agent run {CorrelationId} starting with provider {Provider}, model {Model}, profile {ExecutionProfile}, provider profile {ProviderProfile}, thinking requested {RequestedThinkingMode}, thinking effective {EffectiveThinkingMode}, thinking reason {ThinkingReason}, history messages {HistoryMessageCount}, MCP status {McpStatus}, read-only MCP tools {ReadOnlyToolCount}, confirmation MCP tools {ConfirmationToolCount}.",
            context.CorrelationId,
            health.Provider,
            health.Model,
            executionPlan.Profile,
            executionPlan.Capabilities.ProviderKey,
            executionPlan.RequestedThinkingMode,
            executionPlan.EffectiveThinkingMode,
            executionPlan.Reason,
            context.ConversationMessages.Count,
            homeAssistantMcpTools.Status,
            homeAssistantMcpTools.ExposedToolCount,
            homeAssistantMcpTools.ConfirmationRequiredTools.Count);

        var agent = CreateAgent(_llmOptions.Value, homeAssistantMcpTools, context, executionPlan);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions())
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["correlation_id"] = context.CorrelationId,
            },
        };

        AgentResponse response;
        try
        {
            response = await agent.RunAsync(
                CreateMessages(message, context),
                session: null,
                options: runOptions,
                cancellationToken);
        }
        catch (ClientResultException exception)
        {
            _logger.LogWarning(
                exception,
                "LLM provider request failed with HTTP status {Status}.",
                exception.Status);

            return CreateProviderFailureResponse(
                context,
                health,
                exception.Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Agent runtime failed before returning a response.");

            return CreateProviderFailureResponse(
                context,
                health,
                status: null);
        }

        _logger.LogInformation(
            "Agent run {CorrelationId} completed with response length {ResponseLength}.",
            context.CorrelationId,
            response.Text.Length);

        return new AgentRuntimeResponse(
            context.CorrelationId,
            IsConfigured: true,
            response.Text,
            health);
    }

    private static AgentRuntimeResponse CreateProviderFailureResponse(
        AgentContext context,
        AgentRuntimeHealth health,
        int? status)
    {
        var statusText = status.HasValue
            ? $" HTTP {status.Value}"
            : string.Empty;

        return new AgentRuntimeResponse(
            context.CorrelationId,
            IsConfigured: false,
            $"Не смог получить ответ от LLM provider{statusText}. Запрос не сохранен в историю диалога. Повтори запрос позже; если проверяешь Home Assistant MCP, можно также выполнить /status.",
            health);
    }

    private ChatClientAgent CreateAgent(
        LlmOptions options,
        HomeAssistantMcpAgentToolSet homeAssistantMcpTools,
        AgentContext context,
        LlmExecutionPlan executionPlan)
    {
        var chatClient = new ChatClient(
            model: options.Model,
            credential: new ApiKeyCredential(options.ApiKey),
            options: CreateOpenAIClientOptions(options, executionPlan, _loggerFactory));
        IChatClient aiChatClient = chatClient.AsIChatClient();
        if (executionPlan.UsesTools
            && executionPlan.Capabilities.RequiresReasoningContentRoundTripForToolCalls)
        {
            aiChatClient = new ReasoningContentReplayChatClient(
                aiChatClient,
                _loggerFactory.CreateLogger<ReasoningContentReplayChatClient>());
        }

        var statusTool = AIFunctionFactory.Create(
            (Func<AgentStatusSnapshot>)_statusTool.GetStatus,
            name: "status",
            description: "Returns non-secret application version, uptime, configuration mode, and health details.",
            serializerOptions: null);

        var tools = new List<AITool>();
        if (executionPlan.UsesTools)
        {
            tools.Add(statusTool);

            if (_homeAssistantMcpStatusTool is not null)
            {
                tools.Add(AIFunctionFactory.Create(
                    (Func<CancellationToken, Task<HomeAssistantMcpDiscoveryResult>>)_homeAssistantMcpStatusTool.GetStatusAsync,
                    name: "home_assistant_mcp_status",
                    description: "Returns Home Assistant MCP reachability, auth state, endpoint, and discovered tools/prompts without revealing tokens.",
                    serializerOptions: null));
            }

            tools.AddRange(homeAssistantMcpTools.Tools);

            if (_confirmationService is not null
                && homeAssistantMcpTools.ConfirmationRequiredTools.Count > 0)
            {
                tools.Add(CreateHomeAssistantActionProposalTool(
                    context,
                    homeAssistantMcpTools.ConfirmationRequiredTools));
            }
        }

        return aiChatClient.AsAIAgent(
            instructions: CreateInstructions(homeAssistantMcpTools, executionPlan),
            name: "ha_personal_agent",
            description: "Learning-first personal assistant for Home Assistant.",
            tools: tools,
            loggerFactory: _loggerFactory,
            services: _serviceProvider);
    }

    private static OpenAIClientOptions CreateOpenAIClientOptions(
        LlmOptions options,
        LlmExecutionPlan executionPlan,
        ILoggerFactory loggerFactory)
    {
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.BaseUrl),
        };

        if (executionPlan.ShouldPatchChatCompletionRequest)
        {
            clientOptions.AddPolicy(
                new LlmChatCompletionRequestPolicy(
                    executionPlan,
                    loggerFactory.CreateLogger<LlmChatCompletionRequestPolicy>()),
                PipelinePosition.BeforeTransport);
        }

        return clientOptions;
    }

    private AIFunction CreateHomeAssistantActionProposalTool(
        AgentContext context,
        IReadOnlyCollection<HomeAssistantMcpItemInfo> confirmationRequiredTools)
    {
        Task<ConfirmationProposalResult> ProposeHomeAssistantMcpActionAsync(
            string toolName,
            string argumentsJson,
            string summary,
            string risk,
            CancellationToken cancellationToken)
        {
            var matchedTool = confirmationRequiredTools.FirstOrDefault(tool =>
                string.Equals(tool.Name, toolName, StringComparison.Ordinal));
            if (matchedTool is null)
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected(
                    $"Не могу создать действие: MCP tool '{toolName}' недоступен как confirmation-required tool в текущей Home Assistant session."));
            }

            return _confirmationService!.ProposeAsync(
                new ConfirmationProposalRequest(
                    context,
                    HomeAssistantMcpActionExecutor.HomeAssistantMcpActionKind,
                    matchedTool.Name,
                    argumentsJson,
                    summary,
                    risk),
                cancellationToken);
        }

        return AIFunctionFactory.Create(
            (Func<string, string, string, string, CancellationToken, Task<ConfirmationProposalResult>>)ProposeHomeAssistantMcpActionAsync,
            name: "propose_home_assistant_mcp_action",
            description: "Creates a pending Home Assistant MCP control action. Use exact toolName and JSON object string argumentsJson. Available confirmation-required tools: "
                + FormatToolList(confirmationRequiredTools, maxTools: 20),
            serializerOptions: null);
    }

    private async Task<HomeAssistantMcpAgentToolSet> CreateHomeAssistantMcpToolsAsync(
        LlmExecutionPlan executionPlan,
        CancellationToken cancellationToken)
    {
        if (!executionPlan.UsesTools)
        {
            _logger.LogInformation(
                "Agent run profile {ExecutionProfile} disables all tools for this run.",
                executionPlan.Profile);

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.NotConfigured,
                $"Tools are disabled for {executionPlan.Profile} profile.");
        }

        if (_homeAssistantMcpToolProvider is null)
        {
            _logger.LogInformation(
                "Home Assistant MCP tool provider is not registered; agent run will continue without Home Assistant tools.");

            return HomeAssistantMcpAgentToolSet.Unavailable(
                HomeAssistantMcpStatus.NotConfigured,
                "Home Assistant MCP tool provider is not registered.");
        }

        return await _homeAssistantMcpToolProvider.CreateReadOnlyToolSetAsync(cancellationToken);
    }

    private static IReadOnlyList<AiChatMessage> CreateMessages(string message, AgentContext context)
    {
        var messages = new List<AiChatMessage>(context.ConversationMessages.Count + 1);

        foreach (var conversationMessage in context.ConversationMessages)
        {
            if (string.IsNullOrWhiteSpace(conversationMessage.Text))
            {
                continue;
            }

            messages.Add(new AiChatMessage(
                MapRole(conversationMessage.Role),
                conversationMessage.Text));
        }

        messages.Add(new AiChatMessage(AiChatRole.User, message));

        return messages;
    }

    private static AiChatRole MapRole(AgentConversationRole role) =>
        role switch
        {
            AgentConversationRole.User => AiChatRole.User,
            AgentConversationRole.Assistant => AiChatRole.Assistant,
            _ => AiChatRole.User,
        };

    private string CreateInstructions(
        HomeAssistantMcpAgentToolSet homeAssistantMcpTools,
        LlmExecutionPlan executionPlan)
    {
        var instructions = new StringBuilder(Instructions);

        if (!executionPlan.UsesTools)
        {
            instructions.AppendLine();
            instructions.AppendLine(
                "This run uses a no-tools profile. Do not claim to inspect Home Assistant, call tools, read files, or control devices.");
            instructions.AppendLine(
                "If the user asks for live home state or control, explain that this requires the normal tool-enabled dialogue profile.");
            return instructions.ToString();
        }

        if (_confirmationService is not null
            && homeAssistantMcpTools.ConfirmationRequiredTools.Count > 0)
        {
            instructions.AppendLine();
            instructions.AppendLine(
                "Home Assistant control workflow: prepare actions only through propose_home_assistant_mcp_action with exact toolName, JSON object argumentsJson, short summary, and explicit risk.");
            instructions.AppendLine(
                "After proposal, tell the user to approve or reject with the commands returned by the tool.");
            instructions.AppendLine("Confirmation-required MCP tools: " + FormatToolList(homeAssistantMcpTools.ConfirmationRequiredTools, maxTools: 30));
        }
        else
        {
            instructions.AppendLine();
            instructions.AppendLine("Home Assistant control workflow is unavailable in this run; explain that control requires confirmation.");
        }

        return instructions.ToString();
    }

    private static string FormatToolList(
        IReadOnlyCollection<HomeAssistantMcpItemInfo> tools,
        int maxTools)
    {
        if (tools.Count == 0)
        {
            return "none";
        }

        var formattedTools = tools
            .Take(maxTools)
            .Select(tool => string.IsNullOrWhiteSpace(tool.Description)
                ? tool.Name
                : $"{tool.Name} ({Truncate(tool.Description, 100)})");
        var suffix = tools.Count > maxTools
            ? $" and {tools.Count - maxTools} more"
            : string.Empty;

        return string.Join("; ", formattedTools) + suffix;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];
}
