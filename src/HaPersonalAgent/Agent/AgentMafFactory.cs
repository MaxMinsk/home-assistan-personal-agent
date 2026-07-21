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
    private const int MaximumToolIterationsPerRequest = 6;
    private const int MaximumConsecutiveToolErrorsPerRequest = 1;

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

        // HPA-041 follow-up: общий per-run канал reasoning между capture-клиентом (M.E.AI парсит reasoning_content
        // из ответа) и request-политикой (единственный слой, что доносит его обратно на провод). Опт-ин через
        // llm_replay_reasoning_to_wire (по умолчанию OFF — безопасное поведение), и только когда есть инструменты и
        // провайдер требует round-trip reasoning для tool-шагов; иначе thinking-during-tools неактуален.
        var reasoningStore =
            llmOptions.ReplayReasoningContentToWire
            && executionPlan.UsesTools
            && executionPlan.Capabilities.RequiresReasoningContentRoundTripForToolCalls
                ? new ToolCallReasoningStore()
                : null;

        var chatClient = new ChatClient(
            model: model,
            credential: new ApiKeyCredential(llmOptions.ApiKey),
            options: CreateOpenAIClientOptions(
                llmOptions,
                executionPlan,
                _loggerFactory,
                reasoningDiagnostics,
                reasoningStore));

        // Wire-тест (ReasoningContentWireSerializationTests) доказал: OpenAI-клиент M.E.AI парсит reasoning_content
        // из ответа в TextReasoningContent, но НЕ сериализует его обратно. Поэтому захват reasoning живёт здесь
        // (на уровне M.E.AI), а вписывание — в LlmChatCompletionRequestPolicy (raw JSON); общий канал — reasoningStore.
        IChatClient aiChatClient = chatClient.AsIChatClient();
        if (reasoningStore is not null)
        {
            aiChatClient = new ToolCallReasoningCaptureChatClient(
                aiChatClient,
                reasoningStore,
                _loggerFactory.CreateLogger<ToolCallReasoningCaptureChatClient>());
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
            .UseFunctionInvocation(
                _loggerFactory,
                functionInvokingClient =>
                {
                    functionInvokingClient.MaximumIterationsPerRequest = MaximumToolIterationsPerRequest;
                    functionInvokingClient.MaximumConsecutiveErrorsPerRequest = MaximumConsecutiveToolErrorsPerRequest;
                })
            .Build();

        var tools = _toolCatalog.CreateTools(
            context,
            executionPlan,
            homeAssistantMcpTools).ToList();

        // HPA-036: если у run есть бюджет (фоновые запуски), оборачиваем ВСЕ инструменты —
        // и наши, и пришедшие из MCP — чтобы счётчик был честным, а потолок реально работал.
        if (context.RunBudget is { } runBudget)
        {
            var budgetLogger = _loggerFactory.CreateLogger<BudgetedAIFunction>();
            tools = tools
                .Select(tool => tool is Microsoft.Extensions.AI.AIFunction function
                    ? new BudgetedAIFunction(function, runBudget, budgetLogger)
                    : tool)
                .ToList();
        }
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
        ReasoningRunDiagnostics reasoningDiagnostics,
        ToolCallReasoningStore? reasoningStore)
    {
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.BaseUrl),
        };

        // Политика подключается ВСЕГДА: даже когда thinking-патчи не нужны (provider-default), она чинит
        // некорректный content: "" на assistant-сообщениях с tool_calls, иначе Moonshot валит любой tool-шаг с HTTP 400.
        // reasoningStore (если есть) позволяет ей вписывать захваченный reasoning_content обратно на провод (HPA-041 follow-up).
        clientOptions.AddPolicy(
            new LlmChatCompletionRequestPolicy(
                executionPlan,
                loggerFactory.CreateLogger<LlmChatCompletionRequestPolicy>(),
                reasoningDiagnostics,
                reasoningStore),
            PipelinePosition.BeforeTransport);

        return clientOptions;
    }
}

#pragma warning restore MAAI001
