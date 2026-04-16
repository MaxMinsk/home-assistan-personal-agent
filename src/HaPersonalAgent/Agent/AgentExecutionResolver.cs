using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: resolver execution-решения для одного user запроса.
/// Зачем: разделяет ответственность между orchestration и выбором model/thinking (router + planner), чтобы упрощать тестирование и эволюцию policy.
/// Как: сначала строит routing decision, затем на его основе вычисляет итоговый LlmExecutionPlan и возвращает AgentExecutionDecision.
/// </summary>
public sealed class AgentExecutionResolver
{
    private readonly LlmExecutionRouter _executionRouter;
    private readonly LlmExecutionPlanner _executionPlanner;

    public AgentExecutionResolver(
        LlmExecutionRouter executionRouter,
        LlmExecutionPlanner executionPlanner)
    {
        _executionRouter = executionRouter ?? throw new ArgumentNullException(nameof(executionRouter));
        _executionPlanner = executionPlanner ?? throw new ArgumentNullException(nameof(executionPlanner));
    }

    public AgentExecutionDecision Resolve(
        LlmOptions options,
        AgentContext context,
        string userMessage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        var defaultModel = string.IsNullOrWhiteSpace(options.Model)
            ? "unknown-model"
            : options.Model.Trim();
        var routingDecision = _executionRouter.Decide(
            options,
            context,
            userMessage,
            context.ExecutionProfile);
        var selectedModel = routingDecision.IsApplied
            ? routingDecision.SelectedModel
            : defaultModel;
        var selectedThinkingModeOverride = routingDecision.IsApplied
            ? routingDecision.ThinkingModeOverride
            : null;

        // Extension point: при внедрении classifier-based routing сюда можно добавить weighted confidence
        // и guardrails "never-route" для чувствительных профилей/tools.
        var selectedPlan = _executionPlanner.CreatePlan(
            options,
            context.ExecutionProfile,
            selectedThinkingModeOverride);

        return new AgentExecutionDecision(
            options,
            context,
            userMessage,
            defaultModel,
            routingDecision,
            selectedModel,
            selectedThinkingModeOverride,
            selectedPlan);
    }

    public LlmExecutionPlan BuildFallbackPlan(AgentExecutionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return _executionPlanner.CreatePlan(
            decision.LlmOptions,
            decision.Context.ExecutionProfile,
            decision.SelectedThinkingModeOverride);
    }
}
