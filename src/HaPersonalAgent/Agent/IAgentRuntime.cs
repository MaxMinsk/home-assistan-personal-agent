namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: порт приложения для общения с агентом.
/// Зачем: Telegram, CLI runner и будущие integrations должны вызывать один контракт, не зная деталей MAF/OpenAI/Moonshot.
/// Как: SendAsync принимает user message, AgentContext и cancellation token, а GetHealth сообщает готовность runtime без сетевого вызова.
/// </summary>
public interface IAgentRuntime
{
    AgentRuntimeHealth GetHealth();

    Task<AgentRuntimeResponse> SendAsync(
        string message,
        AgentContext context,
        CancellationToken cancellationToken);
}
