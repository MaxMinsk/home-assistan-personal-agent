namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: роль сообщения в истории диалога агента.
/// Зачем: runtime должен отличать реплики пользователя от ответов assistant при восстановлении контекста.
/// Как: значения мапятся на Microsoft.Extensions.AI.ChatRole перед вызовом Microsoft Agent Framework.
/// </summary>
public enum AgentConversationRole
{
    User,
    Assistant,
}
