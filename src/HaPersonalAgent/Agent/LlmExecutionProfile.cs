namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: тип agent run с точки зрения tools и reasoning.
/// Зачем: один и тот же provider может требовать разных настроек для tool-enabled вызова, обычного чата и deep reasoning.
/// Как: AgentContext передает профиль в AgentRuntime, а LlmExecutionPlanner выбирает effective thinking mode.
/// </summary>
public enum LlmExecutionProfile
{
    ToolEnabled,
    PureChat,
    DeepReasoning,
}
