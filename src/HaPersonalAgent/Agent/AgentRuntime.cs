using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.HomeAssistant;
using HaPersonalAgent.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.ClientModel;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: orchestration-фасад runtime поверх Microsoft Agent Framework.
/// Зачем: transport-слою нужен один порт (`IAgentRuntime`), но внутренняя логика должна быть декомпозирована на отдельные компоненты (resolve/run/fallback/factory/tools).
/// Как: валидирует конфиг, строит execution decision, выполняет run через AgentRunner, применяет fallback policy и возвращает нормализованный AgentRuntimeResponse.
/// </summary>
public sealed class AgentRuntime : IAgentRuntime
{
    private readonly IOptions<LlmOptions> _llmOptions;
    private readonly AgentExecutionResolver _executionResolver;
    private readonly LlmRoutingTelemetry _routingTelemetry;
    private readonly AgentRunner _agentRunner;
    private readonly AgentFallbackExecutor _fallbackExecutor;
    private readonly HomeAssistantMcpToolSetResolver _homeAssistantMcpToolSetResolver;
    private readonly AgentRuntimeDiagnosticsLogger _diagnosticsLogger;
    private readonly ILogger<AgentRuntime> _logger;

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
        LlmRoutingTelemetry? routingTelemetry = null,
        AgentExecutionResolver? executionResolver = null,
        AgentRunner? agentRunner = null,
        AgentFallbackExecutor? fallbackExecutor = null,
        HomeAssistantMcpToolSetResolver? homeAssistantMcpToolSetResolver = null,
        AgentRuntimeDiagnosticsLogger? diagnosticsLogger = null)
    {
        ArgumentNullException.ThrowIfNull(llmOptions);
        ArgumentNullException.ThrowIfNull(statusTool);
        ArgumentNullException.ThrowIfNull(executionPlanner);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var resolvedRouter = executionRouter ?? new LlmExecutionRouter();
        _llmOptions = llmOptions;
        _executionResolver = executionResolver ?? new AgentExecutionResolver(resolvedRouter, executionPlanner);
        _routingTelemetry = routingTelemetry ?? new LlmRoutingTelemetry();
        _fallbackExecutor = fallbackExecutor ?? new AgentFallbackExecutor();
        _logger = loggerFactory.CreateLogger<AgentRuntime>();
        _diagnosticsLogger = diagnosticsLogger ?? new AgentRuntimeDiagnosticsLogger(_logger);
        _homeAssistantMcpToolSetResolver = homeAssistantMcpToolSetResolver ?? new HomeAssistantMcpToolSetResolver(
            homeAssistantMcpToolProvider,
            loggerFactory.CreateLogger<HomeAssistantMcpToolSetResolver>());

        if (agentRunner is not null)
        {
            _agentRunner = agentRunner;
            return;
        }

        var toolCatalog = new AgentToolCatalog(
            statusTool,
            loggerFactory.CreateLogger<AgentToolCatalog>(),
            homeAssistantMcpStatusTool,
            boundedChatHistoryProvider,
            stateRepository,
            confirmationService);
        var compactionPipelineFactory = new AgentCompactionPipelineFactory(
            executionPlanner,
            loggerFactory);
        var mafFactory = new AgentMafFactory(
            toolCatalog,
            compactionPipelineFactory,
            loggerFactory,
            serviceProvider);
        _agentRunner = new AgentRunner(mafFactory);
    }

    public AgentRuntimeHealth GetHealth() =>
        AgentRuntimePreflight.Evaluate(_llmOptions.Value);

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
        var decision = _executionResolver.Resolve(
            llmOptions,
            context,
            message);
        _routingTelemetry.RecordDecision(decision.RoutingDecision);

        var reasoningDiagnostics = new ReasoningRunDiagnostics();
        var compactionDiagnostics = new CompactionRunDiagnostics();
        await using var homeAssistantMcpTools = await _homeAssistantMcpToolSetResolver.CreateAsync(
            decision.SelectedPlan,
            cancellationToken);
        _diagnosticsLogger.LogRunStart(
            context,
            health,
            decision,
            homeAssistantMcpTools);

        AgentResponse response;
        var executedModel = decision.SelectedModel;
        var executedPlan = decision.SelectedPlan;
        var fallbackApplied = false;

        try
        {
            response = await _agentRunner.RunOnceAsync(
                message,
                context,
                llmOptions,
                decision.SelectedModel,
                homeAssistantMcpTools,
                decision.SelectedPlan,
                reasoningDiagnostics,
                compactionDiagnostics,
                onReasoningUpdate,
                cancellationToken);
        }
        catch (ClientResultException exception)
        {
            if (!_fallbackExecutor.TryCreateFallback(
                    decision,
                    exception.Status,
                    out var fallbackContext))
            {
                _logger.LogWarning(
                    exception,
                    "LLM provider request failed with HTTP status {Status}.",
                    exception.Status);
                return CreateFailureResponse(
                    context,
                    health,
                    decision,
                    executedPlan,
                    fallbackApplied,
                    reasoningDiagnostics,
                    compactionDiagnostics,
                    status: exception.Status);
            }

            fallbackApplied = true;
            executedModel = fallbackContext.FallbackModel ?? decision.DefaultModel;
            // Extension point: multi-tier fallback (small -> medium -> default) добавляется внутри AgentFallbackExecutor
            // без изменения orchestration в AgentRuntime.
            executedPlan = _executionResolver.BuildFallbackPlan(decision);

            _logger.LogWarning(
                exception,
                "LLM routed request failed for run {CorrelationId} on model {FailedModel} with HTTP status {Status}; retrying once with fallback model {FallbackModel}.",
                context.CorrelationId,
                decision.SelectedModel,
                exception.Status,
                executedModel);

            try
            {
                response = await _agentRunner.RunOnceAsync(
                    message,
                    context,
                    llmOptions,
                    executedModel,
                    homeAssistantMcpTools,
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
                    "LLM provider request failed with HTTP status {Status} after fallback run.",
                    fallbackException.Status);
                return CreateFailureResponse(
                    context,
                    health,
                    decision,
                    executedPlan,
                    fallbackApplied: true,
                    reasoningDiagnostics,
                    compactionDiagnostics,
                    status: fallbackException.Status);
            }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                _logger.LogWarning(
                    fallbackException,
                    "Agent runtime fallback run failed before returning a response.");
                return CreateFailureResponse(
                    context,
                    health,
                    decision,
                    executedPlan,
                    fallbackApplied: true,
                    reasoningDiagnostics,
                    compactionDiagnostics,
                    status: null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Agent runtime failed before returning a response.");
            return CreateFailureResponse(
                context,
                health,
                decision,
                executedPlan,
                fallbackApplied,
                reasoningDiagnostics,
                compactionDiagnostics,
                status: null);
        }

        var executedBucket = AgentRuntimeResultFactory.ResolveExecutionBucket(
            decision.RoutingDecision,
            executedPlan);
        _routingTelemetry.RecordExecutionBucket(
            executedBucket,
            fallbackApplied);
        _diagnosticsLogger.LogRunCompleted(
            context.CorrelationId,
            response.Text,
            executedModel,
            decision.RoutingDecision.IsApplied,
            fallbackApplied,
            executedBucket);
        _diagnosticsLogger.LogReasoningDiagnostics(context.CorrelationId, executedPlan, reasoningDiagnostics, success: true);
        _diagnosticsLogger.LogCompactionDiagnostics(context.CorrelationId, compactionDiagnostics, success: true);

        return AgentRuntimeResultFactory.CreateSuccessResponse(
            context,
            health,
            response,
            compactionDiagnostics.Snapshot());
    }

    private AgentRuntimeResponse CreateFailureResponse(
        AgentContext context,
        AgentRuntimeHealth health,
        AgentExecutionDecision decision,
        LlmExecutionPlan executedPlan,
        bool fallbackApplied,
        ReasoningRunDiagnostics reasoningDiagnostics,
        CompactionRunDiagnostics compactionDiagnostics,
        int? status)
    {
        var executedBucket = AgentRuntimeResultFactory.ResolveExecutionBucket(
            decision.RoutingDecision,
            executedPlan);
        _routingTelemetry.RecordExecutionBucket(
            executedBucket,
            fallbackApplied);
        _diagnosticsLogger.LogReasoningDiagnostics(context.CorrelationId, executedPlan, reasoningDiagnostics, success: false);
        _diagnosticsLogger.LogCompactionDiagnostics(context.CorrelationId, compactionDiagnostics, success: false);
        return AgentRuntimeResultFactory.CreateProviderFailureResponse(
            context,
            health,
            status);
    }
}
