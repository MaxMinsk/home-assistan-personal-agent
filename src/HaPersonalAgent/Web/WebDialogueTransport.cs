using HaPersonalAgent.Agent;
using HaPersonalAgent.Dialogue;

namespace HaPersonalAgent.Web;

/// <summary>
/// Что: web-специфичная стратегия идентичности диалога и выбора профиля исполнения.
/// Зачем: веб-адаптер должен переиспользовать transport-agnostic DialogueService, задав свой транспорт ("web"), чтобы ключи хранения не пересекались с Telegram.
/// Как: строит DialogueConversation с транспортом "web" (participantId по умолчанию = conversationId) и переводит строковый profile в LlmExecutionProfile.
/// </summary>
public static class WebDialogueTransport
{
    /// <summary>Имя транспорта; входит в storage key (web:conversationId:participantId) и изолирует веб-диалоги от Telegram.</summary>
    public const string Name = "web";

    public static DialogueConversation CreateConversation(string conversationId, string? participantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var participant = string.IsNullOrWhiteSpace(participantId)
            ? conversationId
            : participantId;

        return DialogueConversation.Create(Name, conversationId, participant);
    }

    public static LlmExecutionProfile ResolveExecutionProfile(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return LlmExecutionProfile.ToolEnabled;
        }

        return profile.Trim().ToLowerInvariant() switch
        {
            "deep" or "think" or "reasoning" => LlmExecutionProfile.DeepReasoning,
            "chat" or "pure" => LlmExecutionProfile.PureChat,
            _ => LlmExecutionProfile.ToolEnabled,
        };
    }
}
