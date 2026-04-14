using HaPersonalAgent.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: первая реализация runtime поверх Microsoft Agent Framework.
/// Зачем: нужен учебный vertical slice, который создает MAF ChatClientAgent, подключает Moonshot как OpenAI-compatible endpoint и вызывает model без Telegram.
/// Как: при наличии LLM API key лениво строит OpenAI ChatClient с custom Endpoint, оборачивает его в AsAIAgent и добавляет безопасный status tool.
/// </summary>
public sealed class AgentRuntime : IAgentRuntime
{
    private const string Instructions =
        """
        You are Home Assistant Personal Agent, a learning-first assistant built to explore Microsoft Agent Framework.
        Keep answers concise and practical.
        Use the status tool when the user asks about application status, version, uptime, configuration mode, or health.
        Never reveal secrets or raw tokens.
        """;

    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<LlmOptions> _llmOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly AgentStatusTool _statusTool;
    private readonly object _agentLock = new();
    private ChatClientAgent? _agent;

    public AgentRuntime(
        IOptions<LlmOptions> llmOptions,
        AgentStatusTool statusTool,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        _llmOptions = llmOptions;
        _statusTool = statusTool;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
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

        var agent = GetOrCreateAgent(_llmOptions.Value);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions())
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["correlation_id"] = context.CorrelationId,
            },
        };

        var response = await agent.RunAsync(
            CreateMessages(message, context),
            session: null,
            options: runOptions,
            cancellationToken);

        return new AgentRuntimeResponse(
            context.CorrelationId,
            IsConfigured: true,
            response.Text,
            health);
    }

    private ChatClientAgent GetOrCreateAgent(LlmOptions options)
    {
        if (_agent is not null)
        {
            return _agent;
        }

        lock (_agentLock)
        {
            return _agent ??= CreateAgent(options);
        }
    }

    private ChatClientAgent CreateAgent(LlmOptions options)
    {
        var chatClient = new ChatClient(
            model: options.Model,
            credential: new ApiKeyCredential(options.ApiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(options.BaseUrl),
            });

        var statusTool = AIFunctionFactory.Create(
            (Func<AgentStatusSnapshot>)_statusTool.GetStatus,
            name: "status",
            description: "Returns non-secret application version, uptime, configuration mode, and health details.",
            serializerOptions: null);

        return chatClient.AsAIAgent(
            instructions: Instructions,
            name: "ha_personal_agent",
            description: "Learning-first personal assistant for Home Assistant.",
            tools: [statusTool],
            loggerFactory: _loggerFactory,
            services: _serviceProvider);
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
}
