using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: фабрика сообщений для model invocation.
/// Зачем: преобразование storage-модели в chat-messages должно быть изолировано от runtime orchestration и переиспользуемо в runner/тестах.
/// Как: собирает system blocks (summary/retrieved memory), history turns и текущее user сообщение в порядке, совместимом с MAF/OpenAI chat flow.
/// </summary>
public static class AgentMessageFactory
{
    public static IReadOnlyList<AiChatMessage> CreateMessages(
        string userMessage,
        AgentContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(context);

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

        messages.Add(new AiChatMessage(AiChatRole.User, userMessage));

        return messages;
    }

    private static AiChatRole MapRole(AgentConversationRole role) =>
        role switch
        {
            AgentConversationRole.User => AiChatRole.User,
            AgentConversationRole.Assistant => AiChatRole.Assistant,
            _ => AiChatRole.User,
        };
}
