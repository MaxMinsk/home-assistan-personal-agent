using HaPersonalAgent.Agent;
using HaPersonalAgent.Autonomous;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты моста conversation-агент ↔ плановые агенты (HPA-043/044) и управления очередью контекста (HPA-045).
/// Зачем: реплика из чата должна доезжать в inbox нужного агента с источником Conversation, попадать в его промпт как «контекст из чата», и удаляться по одному.
/// Как: реальный SQLite-репозиторий + сервис + адаптер моста на временной базе, плюс чистая проверка промпта и политики.
/// </summary>
public class AutonomousBridgeTests
{
    [Fact]
    public void Interactive_policy_allows_routing_but_background_research_does_not()
    {
        Assert.True(AgentToolPolicy.Default.AllowScheduledAgentRouting);
        Assert.False(AgentToolPolicy.ReadOnlyResearch(true, true, true).AllowScheduledAgentRouting);
    }

    [Fact]
    public async Task Route_note_lands_in_the_inbox_with_conversation_source()
    {
        await WithBridgeAsync(async (repository, _, bridge) =>
        {
            var agentId = await SeedAgentAsync(repository, "Бизнес-исследование", "Исследуй бизнес-идеи.");

            var routed = await bridge.RouteNoteAsync(agentId, "Пользователь думает про аренду недвижимости.", CancellationToken.None);

            Assert.True(routed);
            var pending = await repository.GetPendingRepliesAsync(agentId, CancellationToken.None);
            var entry = Assert.Single(pending);
            Assert.Equal(AutonomousAgentReplySource.Conversation, entry.Source);
            Assert.Contains("аренду недвижимости", entry.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Route_note_to_unknown_agent_returns_false()
    {
        await WithBridgeAsync(async (_, _, bridge) =>
        {
            Assert.False(await bridge.RouteNoteAsync("nope", "заметка", CancellationToken.None));
        });
    }

    [Fact]
    public async Task List_returns_agents_with_mission_so_the_conversation_agent_can_match()
    {
        await WithBridgeAsync(async (repository, _, bridge) =>
        {
            await SeedAgentAsync(repository, "Бизнес", "Исследуй бизнес.");
            await SeedAgentAsync(repository, "Дом и сад", "Следи за садом.");

            var list = await bridge.ListAsync(CancellationToken.None);

            Assert.Equal(2, list.Count);
            Assert.Contains(list, a => a.Name == "Бизнес" && a.Mission.Contains("бизнес", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task Briefing_reports_never_run_then_the_last_completed_run()
    {
        await WithBridgeAsync(async (repository, _, bridge) =>
        {
            var agentId = await SeedAgentAsync(repository, "Бизнес", "Исследуй бизнес.");

            var beforeRun = await bridge.GetBriefingAsync(agentId, CancellationToken.None);
            Assert.NotNull(beforeRun);
            Assert.False(beforeRun!.HasRun);

            var run = AutonomousAgentRun.Start(agentId);
            await repository.AppendRunAsync(run, CancellationToken.None);
            await repository.UpdateRunAsync(
                run.Complete("Нашёл три ниши.", questionsJson: null, diagnostics: null, toolCallCount: 1),
                CancellationToken.None);
            await repository.UpsertContinuityAsync(
                AutonomousAgentContinuity.Empty(agentId) with { Focus = "Посчитать аренду", OpenQuestions = "Какой бюджет?" },
                CancellationToken.None);

            var afterRun = await bridge.GetBriefingAsync(agentId, CancellationToken.None);
            Assert.NotNull(afterRun);
            Assert.True(afterRun!.HasRun);
            Assert.Equal("Нашёл три ниши.", afterRun.LastSummary);
            Assert.Equal("Посчитать аренду", afterRun.Focus);
            Assert.Single(afterRun.OpenQuestions);
        });
    }

    [Fact]
    public void Prompt_labels_chat_context_separately_from_answers()
    {
        var definition = AutonomousAgentDefinition.Create("Бизнес", "миссия", AutonomousAgentScheduleKind.Weekly);
        var replies = new[]
        {
            AutonomousAgentInboxEntry.Create(definition.Id, "Бюджет 20k.", AutonomousAgentReplySource.Telegram),
            AutonomousAgentInboxEntry.Create(definition.Id, "Думаю про аренду.", AutonomousAgentReplySource.Conversation),
        };

        var prompt = AutonomousAgentPromptBuilder.BuildRunInput(definition, continuity: null, replies, previousSummary: null);

        Assert.Contains("New relevant context the user mentioned in chat", prompt, StringComparison.Ordinal);
        Assert.Contains("Думаю про аренду.", prompt, StringComparison.Ordinal);
        Assert.Contains("NEW answers from the user", prompt, StringComparison.Ordinal);
        Assert.Contains("Бюджет 20k.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pending_inbox_entry_can_be_deleted_before_the_run_consumes_it()
    {
        await WithBridgeAsync(async (repository, _, bridge) =>
        {
            var agentId = await SeedAgentAsync(repository, "Бизнес", "миссия");
            await bridge.RouteNoteAsync(agentId, "заметка A", CancellationToken.None);
            await bridge.RouteNoteAsync(agentId, "заметка B", CancellationToken.None);

            var pending = await repository.GetPendingRepliesAsync(agentId, CancellationToken.None);
            Assert.Equal(2, pending.Count);

            var deleted = await repository.DeletePendingReplyAsync(agentId, pending[0].Id, CancellationToken.None);
            Assert.True(deleted);

            var afterDelete = await repository.GetPendingRepliesAsync(agentId, CancellationToken.None);
            Assert.Single(afterDelete);

            // Повторное удаление той же записи и удаление у чужого агента — no-op.
            Assert.False(await repository.DeletePendingReplyAsync(agentId, pending[0].Id, CancellationToken.None));
            Assert.False(await repository.DeletePendingReplyAsync("other", afterDelete[0].Id, CancellationToken.None));
        });
    }

    private static async Task<string> SeedAgentAsync(
        IAutonomousAgentRepository repository,
        string name,
        string mission)
    {
        var definition = AutonomousAgentDefinition.Create(name, mission, AutonomousAgentScheduleKind.Weekly);
        await repository.UpsertDefinitionAsync(definition, CancellationToken.None);
        return definition.Id;
    }

    private static async Task WithBridgeAsync(
        Func<SqliteAutonomousAgentRepository, AutonomousAgentService, AutonomousScheduledAgentBridge, Task> body)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "ha-personal-agent-tests",
            Guid.NewGuid().ToString("N"),
            "state.sqlite");
        try
        {
            var repository = new SqliteAutonomousAgentRepository(new SqliteConnectionFactory(
                Options.Create(new AgentOptions { StateDatabasePath = databasePath })));
            var service = new AutonomousAgentService(repository, NullLogger<AutonomousAgentService>.Instance);
            var bridge = new AutonomousScheduledAgentBridge(service);

            await body(repository, service, bridge);
        }
        finally
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
