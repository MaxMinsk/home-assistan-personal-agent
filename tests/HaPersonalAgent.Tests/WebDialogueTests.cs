using HaPersonalAgent.Agent;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.Web;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты web-адаптера диалога (HPA-026): идентичность conversation, изоляция storage key от Telegram и маппинг профиля.
/// Зачем: веб-транспорт переиспользует общий DialogueService, поэтому его ключи и профиль — часть runtime-контракта.
/// Как: проверяет чистые методы WebDialogueTransport и что DialogueConversationKey для "web" не совпадает с "telegram".
/// </summary>
public class WebDialogueTests
{
    [Fact]
    public void Transport_name_is_web()
    {
        Assert.Equal("web", WebDialogueTransport.Name);
    }

    [Fact]
    public void Participant_defaults_to_conversation_id_when_missing()
    {
        var conversation = WebDialogueTransport.CreateConversation("session-1", participantId: null);

        Assert.Equal("web", conversation.Transport);
        Assert.Equal("session-1", conversation.ConversationId);
        Assert.Equal("session-1", conversation.ParticipantId);
    }

    [Fact]
    public void Explicit_participant_is_preserved()
    {
        var conversation = WebDialogueTransport.CreateConversation("session-1", "ha-user-7");

        Assert.Equal("ha-user-7", conversation.ParticipantId);
    }

    [Fact]
    public void Web_storage_key_does_not_collide_with_telegram()
    {
        var webKey = DialogueConversationKey.Create(
            WebDialogueTransport.CreateConversation("42", "42"));
        var telegramKey = DialogueConversationKey.Create(
            DialogueConversation.Create("telegram", "42", "42"));

        Assert.StartsWith("web:", webKey);
        Assert.NotEqual(telegramKey, webKey);
    }

    [Theory]
    [InlineData(null, LlmExecutionProfile.ToolEnabled)]
    [InlineData("", LlmExecutionProfile.ToolEnabled)]
    [InlineData("tool", LlmExecutionProfile.ToolEnabled)]
    [InlineData("default", LlmExecutionProfile.ToolEnabled)]
    [InlineData("deep", LlmExecutionProfile.DeepReasoning)]
    [InlineData("THINK", LlmExecutionProfile.DeepReasoning)]
    [InlineData("reasoning", LlmExecutionProfile.DeepReasoning)]
    [InlineData("chat", LlmExecutionProfile.PureChat)]
    public void Profile_resolver_maps_known_values(string? profile, LlmExecutionProfile expected)
    {
        Assert.Equal(expected, WebDialogueTransport.ResolveExecutionProfile(profile));
    }
}
