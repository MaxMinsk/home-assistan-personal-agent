using HaPersonalAgent.Configuration;
using HaPersonalAgent.HomeAssistant;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;

#pragma warning disable MAAI001 // Microsoft.Agents.AI.Compaction is preview in current package.

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: фабрика ChatClientAgent (MAF) для одного run.
/// Зачем: wiring OpenAI-compatible client, middleware и tools не должен быть размазан по runtime orchestration.
/// Как: создает ChatClient, подключает request policy/replay/logging/compaction, затем материализует AIAgent с инструкциями и tools.
/// </summary>
public sealed class AgentMafFactory
{
    private readonly AgentToolCatalog _toolCatalog;
    private readonly AgentCompactionPipelineFactory _compactionPipelineFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;

    public AgentMafFactory(
        AgentToolCatalog toolCatalog,
        AgentCompactionPipelineFactory compactionPipelineFactory,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        _toolCatalog = toolCatalog ?? throw new ArgumentNullException(nameof(toolCatalog));
        _compactionPipelineFactory = compactionPipelineFactory ?? throw new ArgumentNullException(nameof(compactionPipelineFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public ChatClientAgent CreateAgent(
        LlmOptions llmOptions,
        string model,
        HomeAssistantMcpAgentToolSet homeAssistantMcpTools,
        AgentContext context,
        LlmExecutionPlan executionPlan,
        ReasoningRunDiagnostics reasoningDiagnostics,
        CompactionRunDiagnostics compactionDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(llmOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(homeAssistantMcpTools);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(reasoningDiagnostics);
        ArgumentNullException.ThrowIfNull(compactionDiagnostics);

        var chatClient = new ChatClient(
            model: model,
            credential: new ApiKeyCredential(llmOptions.ApiKey),
            options: CreateOpenAIClientOptions(
                llmOptions,
                executionPlan,
                _loggerFactory,
                reasoningDiagnostics));
        IChatClient aiChatClient = chatClient.AsIChatClient();
        if (executionPlan.UsesTools
            && executionPlan.Capabilities.RequiresReasoningContentRoundTripForToolCalls)
        {
            aiChatClient = new ReasoningContentReplayChatClient(
                aiChatClient,
                _loggerFactory.CreateLogger<ReasoningContentReplayChatClient>(),
                reasoningDiagnostics);
        }

        aiChatClient = new LlmRequestLoggingChatClient(
            aiChatClient,
            context.CorrelationId,
            executionPlan,
            _loggerFactory.CreateLogger<LlmRequestLoggingChatClient>());

        aiChatClient = aiChatClient.AsBuilder()
            .UseAIContextProviders(new CompactionProvider(
                _compactionPipelineFactory.CreatePipeline(
                    chatClient,
                    llmOptions,
                    context,
                    compactionDiagnostics),
                stateKey: "ha_compaction_pipeline",
                loggerFactory: _loggerFactory))
            .Build();

        var tools = _toolCatalog.CreateTools(
            context,
            executionPlan,
            homeAssistantMcpTools).ToList();
        var instructions = _toolCatalog.CreateInstructions(
            context,
            executionPlan,
            homeAssistantMcpTools);

        return aiChatClient.AsAIAgent(
            instructions: instructions,
            name: "ha_personal_agent",
            description: "Learning-first personal assistant for Home Assistant.",
            tools: tools,
            loggerFactory: _loggerFactory,
            services: _serviceProvider);
    }

    private static OpenAIClientOptions CreateOpenAIClientOptions(
        LlmOptions options,
        LlmExecutionPlan executionPlan,
        ILoggerFactory loggerFactory,
        ReasoningRunDiagnostics reasoningDiagnostics)
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
                    loggerFactory.CreateLogger<LlmChatCompletionRequestPolicy>(),
                    reasoningDiagnostics),
                PipelinePosition.BeforeTransport);
        }

        return clientOptions;
    }
}

#pragma warning restore MAAI001
