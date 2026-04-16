using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты context profile builder для adaptive routing.
/// Зачем: HAAG-056 требует детерминированный simple-context packing с контролем budget/tool-поведения.
/// Как: проверяет подрезку history/summary, отключение retrieved memory и профиль выполнения в simple-path.
/// </summary>
public class LlmRoutingContextProfileBuilderTests
{
    [Fact]
    public void BuildSimplePacked_trims_history_and_summary_and_disables_tools_when_configured()
    {
        var builder = new LlmRoutingContextProfileBuilder();
        var context = AgentContext.Create(
            conversationMessages: Enumerable.Range(1, 12)
                .Select(index => new AgentConversationMessage(
                    index % 2 == 0 ? AgentConversationRole.Assistant : AgentConversationRole.User,
                    $"message-{index}",
                    DateTimeOffset.UtcNow))
                .ToArray(),
            persistedSummary: new string('s', 4_000),
            retrievedMemoryContext: "retrieved-memory-context",
            retrievedMemoryCount: 3,
            executionProfile: LlmExecutionProfile.ToolEnabled);

        var profile = builder.BuildSimplePacked(
            context,
            "привет",
            new LlmOptions
            {
                RouterSimpleMaxInputChars = 2_000,
                RouterSimpleMaxHistoryMessages = 4,
                RouterSimpleAllowTools = false,
            });

        Assert.Equal(LlmRoutingDecision.ContextProfileSimplePacked, profile.Profile);
        Assert.Equal(4, profile.Context.ConversationMessages.Count);
        Assert.True((profile.Context.PersistedSummary?.Length ?? 0) <= 1_000);
        Assert.Null(profile.Context.RetrievedMemoryContext);
        Assert.Equal(0, profile.Context.RetrievedMemoryCount);
        Assert.Equal(LlmExecutionProfile.PureChat, profile.Context.ExecutionProfile);
        Assert.True(profile.EstimatedInputChars > 0);
    }

    [Fact]
    public void BuildSimplePacked_keeps_tools_profile_when_allowed()
    {
        var builder = new LlmRoutingContextProfileBuilder();
        var context = AgentContext.Create(
            executionProfile: LlmExecutionProfile.ToolEnabled);

        var profile = builder.BuildSimplePacked(
            context,
            "привет",
            new LlmOptions
            {
                RouterSimpleAllowTools = true,
            });

        Assert.Equal(LlmExecutionProfile.ToolEnabled, profile.Context.ExecutionProfile);
    }
}
