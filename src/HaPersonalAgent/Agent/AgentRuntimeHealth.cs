using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: безопасный health-снимок MAF runtime.
/// Зачем: приложение должно стартовать без LLM ключа и явно показывать, почему agent runtime пока не готов.
/// Как: factory-методы копируют только provider/base URL/model и reason, не включая API key.
/// </summary>
public sealed record AgentRuntimeHealth(
    bool IsConfigured,
    string Provider,
    string BaseUrl,
    string Model,
    string? Reason)
{
    public static AgentRuntimeHealth Configured(LlmOptions options) =>
        new(
            IsConfigured: true,
            options.Provider,
            options.BaseUrl,
            options.Model,
            Reason: null);

    public static AgentRuntimeHealth NotConfigured(LlmOptions options, string reason) =>
        new(
            IsConfigured: false,
            options.Provider,
            options.BaseUrl,
            options.Model,
            reason);
}
