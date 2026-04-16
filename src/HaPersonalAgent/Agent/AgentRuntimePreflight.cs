using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: preflight-валидация runtime-конфигурации перед запуском agent run.
/// Зачем: health-проверка должна оставаться детерминированной и изолированной от orchestration/tool wiring логики.
/// Как: анализирует LlmOptions и возвращает configured/not-configured AgentRuntimeHealth с безопасной причиной для пользователя/логов.
/// </summary>
public static class AgentRuntimePreflight
{
    public static AgentRuntimeHealth Evaluate(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

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
}
