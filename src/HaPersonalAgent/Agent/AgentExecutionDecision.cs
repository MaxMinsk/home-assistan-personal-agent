using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: объединенное решение выполнения одного agent run.
/// Зачем: orchestration-слою нужен единый immutable объект, который описывает routing + planner результат без повторного пересчета.
/// Как: формируется AgentExecutionResolver и содержит выбранную модель, thinking override, execution plan и routing diagnostics.
/// </summary>
public sealed record AgentExecutionDecision(
    LlmOptions LlmOptions,
    AgentContext OriginalContext,
    AgentContext EffectiveContext,
    string UserMessage,
    string DefaultModel,
    LlmRoutingDecision RoutingDecision,
    string EffectiveContextProfile,
    string SelectedModel,
    string? SelectedThinkingModeOverride,
    LlmExecutionPlan SelectedPlan)
{
    public bool UsesFallbackEligibleSmallModelPath =>
        RoutingDecision.IsApplied
        && RoutingDecision.UsesSmallModelTarget
        && !string.Equals(SelectedModel, DefaultModel, StringComparison.OrdinalIgnoreCase);
}
