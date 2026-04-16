using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.HomeAssistant;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.Storage;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

#pragma warning disable MAAI001 // Microsoft.Agents.AI.Compaction is preview in current package.

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: первая реализация runtime поверх Microsoft Agent Framework.
/// Зачем: нужен учебный vertical slice, который создает MAF ChatClientAgent, подключает OpenAI-compatible LLM и дает агенту безопасные tools.
/// Как: на каждый run выбирает LLM execution plan, собирает tools по профилю и запускает ChatClientAgent без привязки к Telegram.
/// </summary>
public sealed class AgentRuntime : IAgentRuntime
{
    private const int ProjectCapsuleToolListLimit = 8;
    private const int ConversationMemorySearchDefaultTopK = 4;
    private const int ConversationMemorySearchMaxTopK = 8;
    private static readonly JsonSerializerOptions ToolJsonOptions = new(JsonSerializerDefaults.Web);

    private const string Instructions =
        """
        You are Home Assistant Personal Agent, a learning-first assistant built to explore Microsoft Agent Framework.
        Keep answers concise and practical.
        Use the status tool when the user asks about application status, version, uptime, configuration mode, or health.
        Use the home_assistant_mcp_status tool when the user asks whether MCP is available, why Home Assistant access fails, or which Home Assistant MCP tools are visible.
        Use Home Assistant MCP tools only for read-only questions about current home state, history, or diagnostics.
        For Home Assistant requests that change state, call propose_home_assistant_mcp_action when it is available.
        Use project_capsules_list and project_capsule_get to inspect durable memory capsules for this conversation.
        To create or update durable memory capsules, call propose_project_capsule_upsert and wait for explicit user approval before claiming the update is applied.
        Never claim a Home Assistant control action was executed until the app reports completion after user approval.
        Never reveal secrets or raw tokens.
        """;

    private readonly IConfirmationService? _confirmationService;
    private readonly IHomeAssistantMcpAgentToolProvider? _homeAssistantMcpToolProvider;
    private readonly HomeAssistantMcpStatusTool? _homeAssistantMcpStatusTool;
    private readonly LlmExecutionPlanner _executionPlanner;
    private readonly LlmExecutionRouter _executionRouter;
    private readonly LlmRoutingTelemetry _routingTelemetry;
    private readonly BoundedChatHistoryProvider? _boundedChatHistoryProvider;
    private readonly ILogger<AgentRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<LlmOptions> _llmOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly AgentStateRepository? _stateRepository;
    private readonly AgentStatusTool _statusTool;

    public AgentRuntime(
        IOptions<LlmOptions> llmOptions,
        AgentStatusTool statusTool,
        LlmExecutionPlanner executionPlanner,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        BoundedChatHistoryProvider? boundedChatHistoryProvider = null,
        AgentStateRepository? stateRepository = null,
        IHomeAssistantMcpAgentToolProvider? homeAssistantMcpToolProvider = null,
        IConfirmationService? confirmationService = null,
        HomeAssistantMcpStatusTool? homeAssistantMcpStatusTool = null,
        LlmExecutionRouter? executionRouter = null,
        LlmRoutingTelemetry? routingTelemetry = null)
    {
        _llmOptions = llmOptions;
        _statusTool = statusTool;
        _executionPlanner = executionPlanner;
        _executionRouter = executionRouter ?? new LlmExecutionRouter();
        _routingTelemetry = routingTelemetry ?? new LlmRoutingTelemetry();
        _boundedChatHistoryProvider = boundedChatHistoryProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentRuntime>();
        _serviceProvider = serviceProvider;
        _stateRepository = stateRepository;
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

        if (!LlmRouterModes.IsValid(options.RouterMode))
        {
            return AgentRuntimeHealth.NotConfigured(options, "Llm:RouterMode must be one of: off, shadow, enforced.");
        }

        return AgentRuntimeHealth.Configured(options);
    }

    public async Task<AgentRuntimeResponse> SendAsync(
        string message,
        AgentContext context,
        Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
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

        var llmOptions = _llmOptions.Value;
        var defaultModel = llmOptions.Model.Trim();
        var routingDecision = _executionRouter.Decide(
            llmOptions,
            context,
            message,
            context.ExecutionProfile);
        _routingTelemetry.RecordDecision(routingDecision);

        var routedModel = routingDecision.IsApplied
            ? routingDecision.SelectedModel
            : defaultModel;
        var routedThinkingModeOverride = routingDecision.IsApplied
            ? routingDecision.ThinkingModeOverride
            : null;

        // Extension point: здесь можно добавить per-provider request budgets (tokens/$),
        // чтобы router decision учитывал дневные лимиты и SLA по latency.
        var executionPlan = _executionPlanner.CreatePlan(
            llmOptions,
            context.ExecutionProfile,
            routedThinkingModeOverride);
        var reasoningDiagnostics = new ReasoningRunDiagnostics();
        var compactionDiagnostics = new CompactionRunDiagnostics();
        await using var homeAssistantMcpTools = await CreateHomeAssistantMcpToolsAsync(
            executionPlan,
            cancellationToken);
        _logger.LogInformation(
            "Agent run {CorrelationId} starting with provider {Provider}, default model {DefaultModel}, selected model {SelectedModel}, profile {ExecutionProfile}, provider profile {ProviderProfile}, thinking requested {RequestedThinkingMode}, thinking effective {EffectiveThinkingMode}, thinking reason {ThinkingReason}, router mode {RouterMode}, router applied {RouterApplied}, router model target {RouterModelTarget}, router reasoning target {RouterReasoningTarget}, router decision bucket {RouterDecisionBucket}, router reason {RouterReason}, history messages {HistoryMessageCount}, memory retrieval mode {MemoryRetrievalMode}, persisted summary present {PersistedSummaryPresent}, persisted summary length {PersistedSummaryLength}, retrieved memories {RetrievedMemoryCount}, retrieved memory text length {RetrievedMemoryLength}, messages since persisted summary {MessagesSincePersistedSummary}, persisted summary refresh requested {ShouldRefreshPersistedSummary}, persisted summary refresh forced {ForcePersistedSummaryRefresh}, MCP status {McpStatus}, read-only MCP tools {ReadOnlyToolCount}, confirmation MCP tools {ConfirmationToolCount}.",
            context.CorrelationId,
            health.Provider,
            defaultModel,
            routedModel,
            executionPlan.Profile,
            executionPlan.Capabilities.ProviderKey,
            executionPlan.RequestedThinkingMode,
            executionPlan.EffectiveThinkingMode,
            executionPlan.Reason,
            routingDecision.RouterMode,
            routingDecision.IsApplied,
            routingDecision.ModelTarget,
            routingDecision.ReasoningTarget,
            routingDecision.DecisionBucket,
            routingDecision.Reason,
            context.ConversationMessages.Count,
            context.MemoryRetrievalMode,
            !string.IsNullOrWhiteSpace(context.PersistedSummary),
            context.PersistedSummary?.Length ?? 0,
            context.RetrievedMemoryCount,
            context.RetrievedMemoryContext?.Length ?? 0,
            context.MessagesSincePersistedSummary,
            context.ShouldRefreshPersistedSummary,
            context.ForcePersistedSummaryRefresh,
            homeAssistantMcpTools.Status,
            homeAssistantMcpTools.ExposedToolCount,
            homeAssistantMcpTools.ConfirmationRequiredTools.Count);

        AgentResponse response;
        var executedModel = routedModel;
        var executedPlan = executionPlan;
        var fallbackApplied = false;
        try
        {
            response = await RunAgentOnceAsync(
                message,
                context,
                llmOptions,
                homeAssistantMcpTools,
                routedModel,
                executionPlan,
                reasoningDiagnostics,
                compactionDiagnostics,
                onReasoningUpdate,
                cancellationToken);
        }
        catch (ClientResultException exception) when (
            LlmRoutingFallbackPolicy.CanRetryWithDefaultModel(
                routingDecision,
                routedModel,
                defaultModel,
                exception.Status))
        {
            fallbackApplied = true;
            executedModel = defaultModel;
            // Extension point: если позже добавим multi-tier routing (small -> medium -> default),
            // здесь можно строить следующую "ступень" fallback из policy-таблицы вместо hardcoded default model.
            executedPlan = _executionPlanner.CreatePlan(
                llmOptions,
                context.ExecutionProfile,
                routedThinkingModeOverride);

            _logger.LogWarning(
                exception,
                "LLM routed request failed for run {CorrelationId} on model {FailedModel} with HTTP status {Status}; retrying once with default model {DefaultModel}.",
                context.CorrelationId,
                routedModel,
                exception.Status,
                defaultModel);

            try
            {
                response = await RunAgentOnceAsync(
                    message,
                    context,
                    llmOptions,
                    homeAssistantMcpTools,
                    defaultModel,
                    executedPlan,
                    reasoningDiagnostics,
                    compactionDiagnostics,
                    onReasoningUpdate,
                    cancellationToken);
            }
            catch (ClientResultException fallbackException)
            {
                _logger.LogWarning(
                    fallbackException,
                    "LLM provider request failed with HTTP status {Status} after fallback to default model.",
                    fallbackException.Status);

                _routingTelemetry.RecordExecutionBucket(
                    ResolveExecutionBucket(routingDecision, executedPlan),
                    fallbackApplied: true);
                LogReasoningDiagnostics(context.CorrelationId, executedPlan, reasoningDiagnostics, success: false);
                LogCompactionDiagnostics(context.CorrelationId, compactionDiagnostics, success: false);
                return CreateProviderFailureResponse(
                    context,
                    health,
                    fallbackException.Status);
            }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                _logger.LogWarning(
                    fallbackException,
                    "Agent runtime fallback run failed before returning a response.");

                _routingTelemetry.RecordExecutionBucket(
                    ResolveExecutionBucket(routingDecision, executedPlan),
                    fallbackApplied: true);
                LogReasoningDiagnostics(context.CorrelationId, executedPlan, reasoningDiagnostics, success: false);
                LogCompactionDiagnostics(context.CorrelationId, compactionDiagnostics, success: false);
                return CreateProviderFailureResponse(
                    context,
                    health,
                    status: null);
            }
        }
        catch (ClientResultException exception)
        {
            _logger.LogWarning(
                exception,
                "LLM provider request failed with HTTP status {Status}.",
                exception.Status);

            _routingTelemetry.RecordExecutionBucket(
                ResolveExecutionBucket(routingDecision, executedPlan),
                fallbackApplied);
            LogReasoningDiagnostics(context.CorrelationId, executedPlan, reasoningDiagnostics, success: false);
            LogCompactionDiagnostics(context.CorrelationId, compactionDiagnostics, success: false);
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

            _routingTelemetry.RecordExecutionBucket(
                ResolveExecutionBucket(routingDecision, executedPlan),
                fallbackApplied);
            LogReasoningDiagnostics(context.CorrelationId, executedPlan, reasoningDiagnostics, success: false);
            LogCompactionDiagnostics(context.CorrelationId, compactionDiagnostics, success: false);
            return CreateProviderFailureResponse(
                context,
                health,
                status: null);
        }

        _routingTelemetry.RecordExecutionBucket(
            ResolveExecutionBucket(routingDecision, executedPlan),
            fallbackApplied);
        _logger.LogInformation(
            "Agent run {CorrelationId} completed with response length {ResponseLength}; selected model {SelectedModel}; router applied {RouterApplied}; fallback applied {FallbackApplied}; executed bucket {ExecutedBucket}.",
            context.CorrelationId,
            response.Text.Length,
            executedModel,
            routingDecision.IsApplied,
            fallbackApplied,
            ResolveExecutionBucket(routingDecision, executedPlan));
        LogReasoningDiagnostics(context.CorrelationId, executedPlan, reasoningDiagnostics, success: true);
        LogCompactionDiagnostics(context.CorrelationId, compactionDiagnostics, success: true);
        var compactionSnapshot = compactionDiagnostics.Snapshot();
        var responseText = compactionSnapshot.SummarizationTriggered
            ? BuildSummarizationNotice(compactionSnapshot) + Environment.NewLine + Environment.NewLine + response.Text
            : response.Text;
        var persistedSummaryCandidate = string.IsNullOrWhiteSpace(compactionSnapshot.LatestSummaryText)
            ? null
            : compactionSnapshot.LatestSummaryText;

        return new AgentRuntimeResponse(
            context.CorrelationId,
            IsConfigured: true,
            responseText,
            health,
            persistedSummaryCandidate);
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

    private async Task<AgentResponse> RunAgentOnceAsync(
        string message,
        AgentContext context,
        LlmOptions llmOptions,
        HomeAssistantMcpAgentToolSet homeAssistantMcpTools,
        string model,
        LlmExecutionPlan executionPlan,
        ReasoningRunDiagnostics reasoningDiagnostics,
        CompactionRunDiagnostics compactionDiagnostics,
        Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
        CancellationToken cancellationToken)
    {
        var agent = CreateAgent(
            llmOptions,
            model,
            homeAssistantMcpTools,
            context,
            executionPlan,
            reasoningDiagnostics,
            compactionDiagnostics);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions())
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["correlation_id"] = context.CorrelationId,
            },
        };
        var messages = CreateMessages(message, context);

        if (onReasoningUpdate is null)
        {
            return await agent.RunAsync(
                messages,
                session: null,
                options: runOptions,
                cancellationToken);
        }

        // MAF pattern: stream updates, process intermediate deltas, then assemble AgentResponse.
        // Ref: dotnet/samples/02-agents/Agents/Agent_Step02_StructuredOutput/Program.cs (RunStreamingAsync + ToAgentResponseAsync).
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync(
                           messages,
                           session: null,
                           options: runOptions,
                           cancellationToken).WithCancellation(cancellationToken))
        {
            updates.Add(update);
            var reasoningTextDelta = ExtractReasoningTextDelta(update);
            if (!string.IsNullOrWhiteSpace(reasoningTextDelta))
            {
                await onReasoningUpdate(
                    new AgentRuntimeReasoningUpdate(context.CorrelationId, reasoningTextDelta),
                    cancellationToken);
            }
        }

        return updates.ToAgentResponse();
    }

    private static string ResolveExecutionBucket(
        LlmRoutingDecision routingDecision,
        LlmExecutionPlan executionPlan)
    {
        if (routingDecision.IsApplied)
        {
            return routingDecision.DecisionBucket;
        }

        // Extension point: когда добавим больше routing bucket'ов (например default+disabled или tool-heavy),
        // здесь можно вычислять bucket по фактическому executionPlan/profile, а не сводить всё к двум default веткам.
        return executionPlan.Profile == LlmExecutionProfile.DeepReasoning
            ? LlmRoutingDecision.DecisionBucketDefaultDeep
            : LlmRoutingDecision.DecisionBucketDefaultProviderDefault;
    }

    private ChatClientAgent CreateAgent(
        LlmOptions options,
        string model,
        HomeAssistantMcpAgentToolSet homeAssistantMcpTools,
        AgentContext context,
        LlmExecutionPlan executionPlan,
        ReasoningRunDiagnostics reasoningDiagnostics,
        CompactionRunDiagnostics compactionDiagnostics)
    {
        var chatClient = new ChatClient(
            model: model,
            credential: new ApiKeyCredential(options.ApiKey),
            options: CreateOpenAIClientOptions(
                options,
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
                CreateCompactionPipeline(
                    chatClient,
                    options,
                    context,
                    compactionDiagnostics),
                stateKey: "ha_compaction_pipeline",
                loggerFactory: _loggerFactory))
            .Build();

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

            if (string.Equals(context.MemoryRetrievalMode, AgentOptions.MemoryRetrievalModeOnDemandTool, StringComparison.Ordinal)
                && _boundedChatHistoryProvider is not null)
            {
                tools.Add(CreateConversationMemorySearchTool(context));
            }

            if (_stateRepository is not null)
            {
                tools.Add(CreateProjectCapsulesListTool(context));
                tools.Add(CreateProjectCapsuleGetTool(context));

                if (_confirmationService is not null)
                {
                    tools.Add(CreateProjectCapsuleUpsertProposalTool(context));
                }
            }
        }

        return aiChatClient.AsAIAgent(
            instructions: CreateInstructions(homeAssistantMcpTools, executionPlan, context),
            name: "ha_personal_agent",
            description: "Learning-first personal assistant for Home Assistant.",
            tools: tools,
            loggerFactory: _loggerFactory,
            services: _serviceProvider);
    }

    /// <summary>
    /// Что: собирает compaction pipeline по MAF-паттерну для входящего контекста диалога.
    /// Зачем: HAAG-034 требует перейти от ad-hoc trimming к стандартным стратегиям MAF с atomic grouping tool-call/result.
    /// Как: применяет стратегии от мягкой к агрессивной: ToolResult - Summarization - SlidingWindow - Truncation.
    /// Ссылки:
    /// - https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step18_CompactionPipeline/Program.cs
    /// - https://github.com/microsoft/agent-framework/blob/main/docs/decisions/0019-python-context-compaction-strategy.md
    /// </summary>
    private CompactionStrategy CreateCompactionPipeline(
        ChatClient primaryChatClient,
        LlmOptions llmOptions,
        AgentContext context,
        CompactionRunDiagnostics compactionDiagnostics)
    {
        var summarizationExecutionPlan = _executionPlanner.CreatePlan(
            llmOptions,
            LlmExecutionProfile.Summarization);
        IChatClient summarizationChatClient = primaryChatClient.AsIChatClient();
        summarizationChatClient = new LlmRequestLoggingChatClient(
            summarizationChatClient,
            context.CorrelationId,
            summarizationExecutionPlan,
            _loggerFactory.CreateLogger<LlmRequestLoggingChatClient>());
        summarizationChatClient = new CompactionSummarizationChatClient(
            summarizationChatClient,
            context.CorrelationId,
            compactionDiagnostics,
            _loggerFactory.CreateLogger<CompactionSummarizationChatClient>());
        var strategies = new List<CompactionStrategy>
        {
            // Tool result compaction остается первым, чтобы не разрывать связки вызов/результат.
            new ToolResultCompactionStrategy(CompactionTriggers.MessagesExceed(28)),
        };

        // Summarization запускается, когда DialogueService запрашивает refresh rolling summary:
        // либо summary отсутствует, либо накопился новый "хвост" сообщений после последнего summary.
        if (context.ShouldRefreshPersistedSummary)
        {
            var summarizationTrigger = context.ForcePersistedSummaryRefresh
                ? CompactionTriggers.MessagesExceed(2)
                : CompactionTriggers.MessagesExceed(24);
            var summarizationTarget = context.ForcePersistedSummaryRefresh
                ? CompactionTriggers.MessagesExceed(2)
                : CompactionTriggers.MessagesExceed(24);
            var minimumPreservedGroups = context.ForcePersistedSummaryRefresh
                ? 2
                : 10;
            strategies.Add(new SummarizationCompactionStrategy(
                summarizationChatClient,
                summarizationTrigger,
                minimumPreservedGroups: minimumPreservedGroups,
                summarizationPrompt: CreateSummarizationPrompt(context.PersistedSummary),
                target: summarizationTarget));
        }

        strategies.AddRange(new CompactionStrategy[]
        {
            new SlidingWindowCompactionStrategy(
                CompactionTriggers.TurnsExceed(16),
                minimumPreservedTurns: 8,
                target: CompactionTriggers.TurnsExceed(12)),
            new TruncationCompactionStrategy(
                CompactionTriggers.MessagesExceed(48),
                minimumPreservedGroups: 12,
                target: CompactionTriggers.MessagesExceed(34)),
        });

        return new PipelineCompactionStrategy(strategies);
    }

    private static string CreateSummarizationPrompt(string? persistedSummary)
    {
        var prompt = new StringBuilder(
            """
            Build persisted long-term conversation memory in Russian.
            Return only this markdown structure:

            ## Контекст пользователя
            - ...

            ## Факты и решения
            - ...

            ## Открытые задачи
            - ...

            ## Ограничения и предпочтения
            - ...

            Rules:
            - This is memory for future runs, not a short recap.
            - Prefer concrete entities, values, commitments, and decisions.
            - Keep only facts useful for future turns; remove obvious noise.
            - Do not copy long quotes from dialogue.
            - Do not ask questions and do not address the user directly.
            - Do not include role labels, timestamps, message ids, tokens, secrets, or raw tool outputs.
            - For sections with data, provide 2-6 concise bullets.
            - If no data for a section, write one bullet: "- нет данных".
            - Target 1500-2800 characters, hard max 3200.
            """);

        if (!string.IsNullOrWhiteSpace(persistedSummary))
        {
            prompt.AppendLine();
            prompt.AppendLine(
                "Important: below is existing persisted summary baseline. Preserve its still-relevant facts unless explicitly contradicted by newer context.");
            prompt.AppendLine("Baseline summary:");
            prompt.AppendLine("---");
            prompt.AppendLine(Truncate(persistedSummary.Trim(), 2_500));
            prompt.AppendLine("---");
            prompt.AppendLine(
                "Do not drop baseline facts only because the latest dialogue topic changed.");
        }

        return prompt.ToString();
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

    private void LogReasoningDiagnostics(
        string correlationId,
        LlmExecutionPlan executionPlan,
        ReasoningRunDiagnostics diagnostics,
        bool success)
    {
        var snapshot = diagnostics.Snapshot();

        _logger.LogInformation(
            "Agent run {CorrelationId} reasoning diagnostics: success {Success}, requested {RequestedThinkingMode}, effective {EffectiveThinkingMode}, patch pipeline enabled {PatchPipelineEnabled}, provider reasoning observed {ProviderReasoningObserved}, replay needed {ReplayNeeded}, safety fallback applied {SafetyFallbackApplied}; policy requests {PolicyRequests}, policy no-patch {PolicyNoPatch}, policy forced disable {PolicyForcedDisable}, policy forced enable {PolicyForcedEnable}, policy auto safety disable {PolicyAutoSafetyDisable}; replay requests {ReplayRequests}, replay request tool-call messages {ReplayRequestToolCalls}, replay request missing reasoning {ReplayRequestMissingReasoning}, replay injected {ReplayInjected}, replay responses {ReplayResponses}, replay response tool-call messages {ReplayResponseToolCalls}, replay response missing reasoning {ReplayResponseMissingReasoning}, replay captured {ReplayCaptured}.",
            correlationId,
            success,
            executionPlan.RequestedThinkingMode,
            executionPlan.EffectiveThinkingMode,
            executionPlan.ShouldPatchChatCompletionRequest,
            snapshot.ProviderReasoningObserved,
            snapshot.ReplayWasNeeded,
            snapshot.SafetyFallbackApplied,
            snapshot.PolicyObservedRequests,
            snapshot.PolicyNoPatchRequests,
            snapshot.PolicyForcedDisablePatches,
            snapshot.PolicyForcedEnablePatches,
            snapshot.PolicyAutoSafetyDisablePatches,
            snapshot.ReplayRequestsObserved,
            snapshot.ReplayRequestToolCallMessages,
            snapshot.ReplayRequestMissingToolCallReasoningMessages,
            snapshot.ReplayInjectedMessages,
            snapshot.ReplayResponsesObserved,
            snapshot.ReplayResponseToolCallMessages,
            snapshot.ReplayResponseMissingToolCallReasoningMessages,
            snapshot.ReplayCapturedMessages);
    }

    private void LogCompactionDiagnostics(
        string correlationId,
        CompactionRunDiagnostics diagnostics,
        bool success)
    {
        var snapshot = diagnostics.Snapshot();

        _logger.LogInformation(
            "Agent run {CorrelationId} compaction diagnostics: success {Success}, summarization requests {SummarizationRequests}, summarization responses {SummarizationResponses}, summarization triggered {SummarizationTriggered}, summary text length {SummaryTextLength}.",
            correlationId,
            success,
            snapshot.SummarizationRequests,
            snapshot.SummarizationResponses,
            snapshot.SummarizationTriggered,
            snapshot.LatestSummaryText?.Length ?? 0);
    }

    private static string BuildSummarizationNotice(CompactionRunDiagnosticsSnapshot snapshot) =>
        $"[context-summary] Чтобы удержать бюджет контекста, я сжал раннюю часть диалога ({snapshot.SummarizationRequests} summarize step).";

    private static string ExtractReasoningTextDelta(AgentResponseUpdate update)
    {
        var reasoningText = string.Concat(
            update.Contents
                .OfType<TextReasoningContent>()
                .Select(content => content.Text ?? string.Empty));

        return reasoningText;
    }

    private AIFunction CreateConversationMemorySearchTool(AgentContext context)
    {
        async Task<string> SearchConversationMemoryAsync(
            string query,
            int topK,
            CancellationToken cancellationToken)
        {
            if (_boundedChatHistoryProvider is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Bounded chat history provider is not registered.",
                }, ToolJsonOptions);
            }

            if (string.IsNullOrWhiteSpace(context.ConversationKey))
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Conversation scope is missing for this run.",
                }, ToolJsonOptions);
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return JsonSerializer.Serialize(new
                {
                    available = true,
                    count = 0,
                    hits = Array.Empty<object>(),
                    reason = "query must be non-empty.",
                }, ToolJsonOptions);
            }

            var normalizedTopK = Math.Clamp(topK <= 0 ? ConversationMemorySearchDefaultTopK : topK, 1, ConversationMemorySearchMaxTopK);
            var hits = await _boundedChatHistoryProvider.SearchAsync(
                context.ConversationKey,
                query,
                normalizedTopK,
                cancellationToken);
            _logger.LogInformation(
                "On-demand conversation memory search for run {CorrelationId}: mode {MemoryRetrievalMode}, topK {TopK}, query length {QueryLength}, hits {HitCount}.",
                context.CorrelationId,
                context.MemoryRetrievalMode,
                normalizedTopK,
                query.Length,
                hits.Count);

            return JsonSerializer.Serialize(new
            {
                available = true,
                count = hits.Count,
                query = NormalizeSingleLine(query, maxLength: 240),
                topK = normalizedTopK,
                hits = hits.Select(hit => new
                {
                    sourceMessageId = hit.SourceMessageId,
                    role = hit.Role == AgentConversationRole.User ? "user" : "assistant",
                    score = Math.Round(hit.Score, 4),
                    text = NormalizeSingleLine(hit.Text, maxLength: 320),
                }),
            }, ToolJsonOptions);
        }

        return AIFunctionFactory.Create(
            (Func<string, int, CancellationToken, Task<string>>)SearchConversationMemoryAsync,
            name: "search_conversation_memory",
            description: "Searches older vector-overflow memory for this conversation. Use when memory retrieval mode is on_demand_tool.",
            serializerOptions: null);
    }

    private AIFunction CreateProjectCapsulesListTool(AgentContext context)
    {
        async Task<string> ListProjectCapsulesAsync(CancellationToken cancellationToken)
        {
            if (_stateRepository is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Project capsule storage is not registered.",
                }, ToolJsonOptions);
            }

            if (string.IsNullOrWhiteSpace(context.ConversationKey))
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Conversation scope is missing for this run.",
                }, ToolJsonOptions);
            }

            var capsules = await _stateRepository.GetProjectCapsulesAsync(
                context.ConversationKey,
                ProjectCapsuleToolListLimit,
                cancellationToken);
            var payload = new
            {
                available = true,
                count = capsules.Count,
                capsules = capsules.Select(capsule => new
                {
                    key = capsule.CapsuleKey,
                    title = capsule.Title,
                    scope = capsule.Scope,
                    confidence = Math.Round(capsule.Confidence, 3),
                    sourceEventId = capsule.SourceEventId,
                    version = capsule.Version,
                    updatedAtUtc = capsule.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    contentMarkdown = capsule.ContentMarkdown,
                }),
            };

            return JsonSerializer.Serialize(payload, ToolJsonOptions);
        }

        return AIFunctionFactory.Create(
            (Func<CancellationToken, Task<string>>)ListProjectCapsulesAsync,
            name: "project_capsules_list",
            description: $"Returns up to {ProjectCapsuleToolListLimit} latest project capsules for this conversation as JSON.",
            serializerOptions: null);
    }

    private AIFunction CreateProjectCapsuleGetTool(AgentContext context)
    {
        async Task<string> GetProjectCapsuleAsync(string capsuleKey, CancellationToken cancellationToken)
        {
            if (_stateRepository is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Project capsule storage is not registered.",
                }, ToolJsonOptions);
            }

            if (string.IsNullOrWhiteSpace(context.ConversationKey))
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Conversation scope is missing for this run.",
                }, ToolJsonOptions);
            }

            var normalizedCapsuleKey = NormalizeCapsuleKey(capsuleKey);
            if (string.IsNullOrWhiteSpace(normalizedCapsuleKey))
            {
                return JsonSerializer.Serialize(new
                {
                    available = true,
                    found = false,
                    reason = "capsuleKey must be non-empty.",
                }, ToolJsonOptions);
            }

            var capsule = await _stateRepository.GetProjectCapsuleByKeyAsync(
                context.ConversationKey,
                normalizedCapsuleKey,
                cancellationToken);
            if (capsule is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = true,
                    found = false,
                    key = normalizedCapsuleKey,
                }, ToolJsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                available = true,
                found = true,
                capsule = new
                {
                    key = capsule.CapsuleKey,
                    title = capsule.Title,
                    scope = capsule.Scope,
                    confidence = Math.Round(capsule.Confidence, 3),
                    sourceEventId = capsule.SourceEventId,
                    version = capsule.Version,
                    updatedAtUtc = capsule.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    contentMarkdown = capsule.ContentMarkdown,
                },
            }, ToolJsonOptions);
        }

        return AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<string>>)GetProjectCapsuleAsync,
            name: "project_capsule_get",
            description: "Returns one project capsule by capsuleKey (ASCII snake_case preferred) as JSON for this conversation.",
            serializerOptions: null);
    }

    private AIFunction CreateProjectCapsuleUpsertProposalTool(AgentContext context)
    {
        Task<ConfirmationProposalResult> ProposeProjectCapsuleUpsertAsync(
            string capsuleKey,
            string title,
            string contentMarkdown,
            string scope,
            double confidence,
            string summary,
            string risk,
            CancellationToken cancellationToken)
        {
            if (_confirmationService is null)
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("Confirmation service сейчас недоступен."));
            }

            var normalizedCapsuleKey = NormalizeCapsuleKey(capsuleKey);
            if (string.IsNullOrWhiteSpace(normalizedCapsuleKey))
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("capsuleKey должен быть непустым и в формате ASCII snake_case."));
            }

            var normalizedTitle = NormalizeSingleLine(title, maxLength: 120);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("title не может быть пустым."));
            }

            var normalizedContentMarkdown = NormalizeMarkdown(contentMarkdown, maxLength: 2_000);
            if (string.IsNullOrWhiteSpace(normalizedContentMarkdown))
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("contentMarkdown не может быть пустым."));
            }

            var normalizedScope = NormalizeSingleLine(scope, maxLength: 80);
            if (string.IsNullOrWhiteSpace(normalizedScope))
            {
                normalizedScope = "conversation";
            }

            var payloadJson = JsonSerializer.Serialize(new
            {
                capsuleKey = normalizedCapsuleKey,
                title = normalizedTitle,
                contentMarkdown = normalizedContentMarkdown,
                scope = normalizedScope,
                confidence = Math.Clamp(confidence, 0d, 1d),
            }, ToolJsonOptions);

            return _confirmationService.ProposeAsync(
                new ConfirmationProposalRequest(
                    context,
                    ProjectCapsuleUpsertActionExecutor.ProjectCapsuleUpsertActionKind,
                    OperationName: $"upsert_project_capsule:{normalizedCapsuleKey}",
                    payloadJson,
                    summary,
                    risk),
                cancellationToken);
        }

        return AIFunctionFactory.Create(
            (Func<string, string, string, string, double, string, string, CancellationToken, Task<ConfirmationProposalResult>>)ProposeProjectCapsuleUpsertAsync,
            name: "propose_project_capsule_upsert",
            description: "Creates pending confirmation to create/update one project capsule. Arguments: capsuleKey, title, contentMarkdown, scope, confidence(0..1), summary, risk.",
            serializerOptions: null);
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
        var messages = new List<AiChatMessage>(context.ConversationMessages.Count + 3);

        if (!string.IsNullOrWhiteSpace(context.PersistedSummary))
        {
            messages.Add(new AiChatMessage(
                AiChatRole.System,
                """
                Persisted conversation summary from previous turns.
                Use it as context, but always prioritize explicit user corrections and the newest dialogue turns.
                Summary:
                """ + Environment.NewLine + context.PersistedSummary));
        }

        if (!string.IsNullOrWhiteSpace(context.RetrievedMemoryContext))
        {
            messages.Add(new AiChatMessage(
                AiChatRole.System,
                context.RetrievedMemoryContext));
        }

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
        LlmExecutionPlan executionPlan,
        AgentContext context)
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

        instructions.AppendLine();
        instructions.AppendLine("Conversation memory retrieval mode: " + context.MemoryRetrievalMode + ".");
        if (string.Equals(context.MemoryRetrievalMode, AgentOptions.MemoryRetrievalModeOnDemandTool, StringComparison.Ordinal))
        {
            if (_boundedChatHistoryProvider is null)
            {
                instructions.AppendLine("On-demand memory search tool is unavailable in this runtime.");
            }
            else
            {
                instructions.AppendLine("Older vector memory is not auto-injected in this mode; call search_conversation_memory when historical facts are needed.");
            }
        }
        else
        {
            instructions.AppendLine("Older relevant vector memories are auto-injected as context before this run.");
        }

        instructions.AppendLine();
        instructions.AppendLine("Use project_capsules_list/project_capsule_get when you need long-term project facts from capsule memory.");

        if (_stateRepository is null)
        {
            instructions.AppendLine("Project capsule storage is unavailable in this runtime; explain that capsule tools are disabled.");
        }
        else if (_confirmationService is null)
        {
            instructions.AppendLine("Project capsule write workflow is unavailable because confirmation service is not registered.");
        }
        else
        {
            instructions.AppendLine("For capsule writes, use propose_project_capsule_upsert and wait for user approval before confirming any memory update.");
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

    private static string NormalizeCapsuleKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(capacity: key.Length);
        var previousIsUnderscore = false;
        foreach (var character in key.Trim().ToLowerInvariant())
        {
            var nextCharacter = char.IsLetterOrDigit(character)
                ? character
                : '_';
            if (nextCharacter == '_')
            {
                if (previousIsUnderscore)
                {
                    continue;
                }

                previousIsUnderscore = true;
                normalized.Append(nextCharacter);
                continue;
            }

            previousIsUnderscore = false;
            normalized.Append(nextCharacter);
        }

        return normalized
            .ToString()
            .Trim('_');
    }

    private static string NormalizeSingleLine(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }

    private static string NormalizeMarkdown(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        while (normalized.Contains("\n\n\n", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];
}

#pragma warning restore MAAI001
