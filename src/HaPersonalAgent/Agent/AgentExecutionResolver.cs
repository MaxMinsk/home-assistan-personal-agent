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
    private readonly LlmRoutingContextProfileBuilder _contextProfileBuilder;

    public AgentExecutionResolver(
        LlmExecutionRouter executionRouter,
        LlmExecutionPlanner executionPlanner,
        LlmRoutingContextProfileBuilder? contextProfileBuilder = null)
    {
        _executionRouter = executionRouter ?? throw new ArgumentNullException(nameof(executionRouter));
        _executionPlanner = executionPlanner ?? throw new ArgumentNullException(nameof(executionPlanner));
        _contextProfileBuilder = contextProfileBuilder ?? new LlmRoutingContextProfileBuilder();
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
        var effectiveContextProfile = LlmRoutingDecision.ContextProfileDefaultFull;
        var effectiveContext = context;
        if (routingDecision.IsApplied
            && string.Equals(routingDecision.ContextProfile, LlmRoutingDecision.ContextProfileSimplePacked, StringComparison.Ordinal))
        {
            var simplePackedProfile = _contextProfileBuilder.BuildSimplePacked(
                context,
                userMessage,
                options);
            effectiveContext = simplePackedProfile.Context;
            effectiveContextProfile = simplePackedProfile.Profile;
        }

        // Extension point: при внедрении classifier-based routing сюда можно добавить weighted confidence
        // и guardrails "never-route" для чувствительных профилей/tools.
        var selectedPlan = _executionPlanner.CreatePlan(
            options,
            effectiveContext.ExecutionProfile,
            selectedThinkingModeOverride);

        return new AgentExecutionDecision(
            options,
            context,
            effectiveContext,
            userMessage,
            defaultModel,
            routingDecision,
            effectiveContextProfile,
            selectedModel,
            selectedThinkingModeOverride,
            selectedPlan);
    }

    public LlmExecutionPlan BuildFallbackPlan(AgentExecutionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return _executionPlanner.CreatePlan(
            decision.LlmOptions,
            decision.EffectiveContext.ExecutionProfile,
            decision.SelectedThinkingModeOverride);
    }
}
