using HaPersonalAgent.Autonomous;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты планировщика автономных агентов и разбора структурированного вывода (HPA-029/HPA-030).
/// Зачем: пропуск, дублирование или наложение фоновых запусков — самые дорогие ошибки подсистемы, их нельзя ловить вручную.
/// Как: in-memory фейк репозитория + фейковый исполнитель + явное «сейчас» в TickAsync, без ожидания реального тика.
/// </summary>
public class AutonomousAgentSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Due_agent_is_run_and_its_schedule_is_advanced_before_the_run()
    {
        var repository = new FakeAutonomousAgentRepository();
        var agent = await SeedAgentAsync(repository, AutonomousAgentScheduleKind.Daily, nextRun: Now.AddMinutes(-1));
        var runner = new FakeAutonomousAgentRunner();

        await CreateScheduler(repository, runner).TickAsync(Now, CancellationToken.None);

        Assert.Single(runner.RunAgentIds);
        Assert.Equal(agent.Id, runner.RunAgentIds[0]);

        var updated = await repository.GetDefinitionAsync(agent.Id, CancellationToken.None);
        Assert.Equal(Now.AddDays(1), updated!.NextRunUtc);
        Assert.Equal(Now, updated.LastRunUtc);
    }

    [Fact]
    public async Task Agent_without_a_next_run_is_seeded_and_runs_immediately()
    {
        var repository = new FakeAutonomousAgentRepository();
        var agent = await SeedAgentAsync(repository, AutonomousAgentScheduleKind.Weekly, nextRun: null);
        var runner = new FakeAutonomousAgentRunner();

        await CreateScheduler(repository, runner).TickAsync(Now, CancellationToken.None);

        Assert.Equal(new[] { agent.Id }, runner.RunAgentIds);
    }

    [Fact]
    public async Task Paused_and_manual_agents_are_never_auto_run()
    {
        var repository = new FakeAutonomousAgentRepository();
        var paused = await SeedAgentAsync(repository, AutonomousAgentScheduleKind.Daily, nextRun: Now.AddMinutes(-5));
        await repository.UpsertDefinitionAsync(
            paused with { Status = AutonomousAgentStatus.Paused },
            CancellationToken.None);
        await SeedAgentAsync(repository, AutonomousAgentScheduleKind.Manual, nextRun: Now.AddMinutes(-5));
        var runner = new FakeAutonomousAgentRunner();

        await CreateScheduler(repository, runner).TickAsync(Now, CancellationToken.None);

        Assert.Empty(runner.RunAgentIds);
    }

    [Fact]
    public async Task Agent_with_a_run_already_in_flight_is_skipped()
    {
        var repository = new FakeAutonomousAgentRepository();
        var agent = await SeedAgentAsync(repository, AutonomousAgentScheduleKind.Hourly, nextRun: Now.AddMinutes(-1));
        await repository.AppendRunAsync(AutonomousAgentRun.Start(agent.Id), CancellationToken.None);
        var runner = new FakeAutonomousAgentRunner();

        await CreateScheduler(repository, runner).TickAsync(Now, CancellationToken.None);

        Assert.Empty(runner.RunAgentIds);
    }

    [Fact]
    public async Task Missed_slot_is_rescheduled_without_running_when_catch_up_is_skip()
    {
        var repository = new FakeAutonomousAgentRepository();
        var agent = await SeedAgentAsync(
            repository,
            AutonomousAgentScheduleKind.Daily,
            nextRun: Now.AddDays(-3));
        var runner = new FakeAutonomousAgentRunner();
        var scheduler = CreateScheduler(repository, runner, catchUpPolicy: AutonomousAgentOptions.CatchUpSkip);

        await scheduler.TickAsync(Now, CancellationToken.None);

        Assert.Empty(runner.RunAgentIds);
        var updated = await repository.GetDefinitionAsync(agent.Id, CancellationToken.None);
        Assert.Equal(Now.AddDays(1), updated!.NextRunUtc);
    }

    [Fact]
    public async Task Missed_slot_runs_once_when_catch_up_is_run_once()
    {
        var repository = new FakeAutonomousAgentRepository();
        var agent = await SeedAgentAsync(
            repository,
            AutonomousAgentScheduleKind.Daily,
            nextRun: Now.AddDays(-3));
        var runner = new FakeAutonomousAgentRunner();

        await CreateScheduler(repository, runner).TickAsync(Now, CancellationToken.None);

        Assert.Equal(new[] { agent.Id }, runner.RunAgentIds);
    }

    [Theory]
    [InlineData(AutonomousAgentScheduleKind.Hourly, 1)]
    [InlineData(AutonomousAgentScheduleKind.Daily, 24)]
    [InlineData(AutonomousAgentScheduleKind.Weekly, 24 * 7)]
    public void Preset_schedules_advance_by_their_interval(AutonomousAgentScheduleKind kind, int expectedHours)
    {
        var next = AutonomousAgentScheduleCalculator.ComputeNextRun(kind, null, Now);

        Assert.Equal(Now.AddHours(expectedHours), next);
    }

    [Fact]
    public void Manual_schedule_never_produces_a_next_run()
    {
        Assert.Null(AutonomousAgentScheduleCalculator.ComputeNextRun(
            AutonomousAgentScheduleKind.Manual,
            null,
            Now));
    }

    [Fact]
    public void Cron_schedule_is_parsed_and_invalid_expressions_are_rejected()
    {
        // Каждый понедельник в 09:00 UTC; 2026-07-20 12:00 UTC — понедельник, значит следующий срок через неделю.
        var next = AutonomousAgentScheduleCalculator.ComputeNextRun(
            AutonomousAgentScheduleKind.Cron,
            "0 9 * * 1",
            Now);

        Assert.NotNull(next);
        Assert.Equal(9, next!.Value.Hour);
        Assert.Equal(DayOfWeek.Monday, next.Value.DayOfWeek);
        Assert.True(next > Now);

        Assert.True(AutonomousAgentScheduleCalculator.IsValidCronExpression("*/15 * * * *"));
        Assert.False(AutonomousAgentScheduleCalculator.IsValidCronExpression("not a cron"));
        Assert.Null(AutonomousAgentScheduleCalculator.ComputeNextRun(
            AutonomousAgentScheduleKind.Cron,
            "not a cron",
            Now));
    }

    [Fact]
    public void Run_output_parser_reads_a_fenced_json_object()
    {
        const string response = """
            Вот результат исследования:
            ```json
            {
              "summary": "Три ниши выглядят реалистично.",
              "questions": ["Интересует B2B?", "Бюджет до 10k?"],
              "durableFacts": ["Факт, который стоит запомнить"],
              "nextFocus": "Посчитать unit-экономику кофейни"
            }
            ```
            """;

        var output = AutonomousRunOutputParser.Parse(response, maxDurableFacts: 3);

        Assert.Equal("Три ниши выглядят реалистично.", output.Summary);
        Assert.Equal(2, output.Questions.Count);
        Assert.Single(output.DurableFacts);
        Assert.Equal("Посчитать unit-экономику кофейни", output.NextFocus);
    }

    [Fact]
    public void Run_output_parser_caps_questions_and_durable_facts()
    {
        const string response = """
            {
              "summary": "s",
              "questions": ["q1", "q2", "q3", "q4", "q5"],
              "durableFacts": ["f1", "f2", "f3", "f4"]
            }
            """;

        var output = AutonomousRunOutputParser.Parse(response, maxDurableFacts: 2);

        Assert.Equal(3, output.Questions.Count);
        Assert.Equal(2, output.DurableFacts.Count);
    }

    [Fact]
    public void Run_output_parser_falls_back_to_prose_instead_of_losing_the_run()
    {
        var output = AutonomousRunOutputParser.Parse("Модель ответила обычным текстом.", maxDurableFacts: 3);

        Assert.Equal("Модель ответила обычным текстом.", output.Summary);
        Assert.Empty(output.Questions);
        Assert.Empty(output.DurableFacts);
    }

    [Fact]
    public void Run_output_parser_handles_braces_inside_strings()
    {
        const string response = """{"summary": "use {curly} braces", "questions": []}""";

        var output = AutonomousRunOutputParser.Parse(response, maxDurableFacts: 1);

        Assert.Equal("use {curly} braces", output.Summary);
    }

    [Fact]
    public void Prompt_includes_mission_pending_replies_and_previous_focus()
    {
        var definition = AutonomousAgentDefinition.Create(
            "Тестовый агент",
            "Тестовая миссия.",
            AutonomousAgentScheduleKind.Weekly);
        var continuity = new AutonomousAgentContinuity(
            definition.Id,
            "Посчитать аренду",
            "Какой бюджет?",
            null,
            null,
            Now);
        var replies = new[]
        {
            AutonomousAgentInboxEntry.Create(definition.Id, "Бюджет 20k USD.", AutonomousAgentReplySource.Telegram),
        };

        var prompt = AutonomousAgentPromptBuilder.BuildRunInput(definition, continuity, replies, "Прошлая сводка");

        Assert.Contains("Тестовая миссия.", prompt, StringComparison.Ordinal);
        Assert.Contains("Бюджет 20k USD.", prompt, StringComparison.Ordinal);
        Assert.Contains("Посчитать аренду", prompt, StringComparison.Ordinal);
        Assert.Contains("Прошлая сводка", prompt, StringComparison.Ordinal);
        Assert.Contains("\"summary\"", prompt, StringComparison.Ordinal);
        // Пользователя нет рядом — агент не должен просить подтверждений во время запуска.
        Assert.Contains("user is NOT present", prompt, StringComparison.Ordinal);
    }

    private static AutonomousAgentScheduler CreateScheduler(
        IAutonomousAgentRepository repository,
        IAutonomousAgentRunner runner,
        string catchUpPolicy = AutonomousAgentOptions.CatchUpRunOnce) =>
        new(
            repository,
            runner,
            Options.Create(new AutonomousAgentOptions
            {
                Enabled = true,
                CatchUpPolicy = catchUpPolicy,
                MaxConcurrentRuns = 2,
                RunTimeoutMinutes = 5,
            }),
            NullLogger<AutonomousAgentScheduler>.Instance);

    private static async Task<AutonomousAgentDefinition> SeedAgentAsync(
        IAutonomousAgentRepository repository,
        AutonomousAgentScheduleKind scheduleKind,
        DateTimeOffset? nextRun)
    {
        var definition = AutonomousAgentDefinition.Create("agent", "миссия", scheduleKind) with
        {
            NextRunUtc = nextRun,
        };
        await repository.UpsertDefinitionAsync(definition, CancellationToken.None);
        return definition;
    }

    /// <summary>Фейковый исполнитель: только фиксирует, кого планировщик решил запустить.</summary>
    private sealed class FakeAutonomousAgentRunner : IAutonomousAgentRunner
    {
        public List<string> RunAgentIds { get; } = new();

        public Task RunAsync(AutonomousAgentDefinition definition, CancellationToken cancellationToken)
        {
            RunAgentIds.Add(definition.Id);
            return Task.CompletedTask;
        }
    }

    /// <summary>In-memory реализация хранилища — достаточная для проверки решений планировщика.</summary>
    private sealed class FakeAutonomousAgentRepository : IAutonomousAgentRepository
    {
        private readonly Dictionary<string, AutonomousAgentDefinition> _definitions = new(StringComparer.Ordinal);
        private readonly List<AutonomousAgentRun> _runs = new();
        private readonly List<AutonomousAgentInboxEntry> _inbox = new();
        private readonly Dictionary<string, AutonomousAgentContinuity> _continuity = new(StringComparer.Ordinal);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertDefinitionAsync(AutonomousAgentDefinition definition, CancellationToken cancellationToken)
        {
            _definitions[definition.Id] = definition;
            return Task.CompletedTask;
        }

        public Task<AutonomousAgentDefinition?> GetDefinitionAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_definitions.TryGetValue(agentId, out var definition) ? definition : null);

        public Task<IReadOnlyList<AutonomousAgentDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutonomousAgentDefinition>>(_definitions.Values.ToList());

        public Task<bool> DeleteDefinitionAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_definitions.Remove(agentId));

        public Task UpdateScheduleStateAsync(
            string agentId,
            DateTimeOffset? nextRunUtc,
            DateTimeOffset? lastRunUtc,
            CancellationToken cancellationToken)
        {
            if (_definitions.TryGetValue(agentId, out var definition))
            {
                _definitions[agentId] = definition with { NextRunUtc = nextRunUtc, LastRunUtc = lastRunUtc };
            }

            return Task.CompletedTask;
        }

        public Task AppendRunAsync(AutonomousAgentRun run, CancellationToken cancellationToken)
        {
            _runs.Add(run);
            return Task.CompletedTask;
        }

        public Task UpdateRunAsync(AutonomousAgentRun run, CancellationToken cancellationToken)
        {
            var index = _runs.FindIndex(candidate => candidate.Id == run.Id);
            if (index >= 0)
            {
                _runs[index] = run;
            }

            return Task.CompletedTask;
        }

        public Task<AutonomousAgentRun?> GetRunAsync(string runId, CancellationToken cancellationToken) =>
            Task.FromResult(_runs.FirstOrDefault(run => run.Id == runId));

        public Task<IReadOnlyList<AutonomousAgentRun>> ListRunsAsync(
            string agentId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutonomousAgentRun>>(_runs
                .Where(run => run.AgentId == agentId)
                .OrderByDescending(run => run.StartedUtc)
                .Take(limit)
                .ToList());

        public Task<bool> HasRunningRunAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_runs.Any(run =>
                run.AgentId == agentId && run.Status == AutonomousAgentRunStatus.Running));

        public Task<AutonomousAgentRun?> FindRunByDeliveredMessageAsync(
            string deliveredMessageId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_runs.FirstOrDefault(run => run.DeliveredMessageId == deliveredMessageId));

        public Task EnqueueReplyAsync(AutonomousAgentInboxEntry entry, CancellationToken cancellationToken)
        {
            _inbox.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AutonomousAgentInboxEntry>> GetPendingRepliesAsync(
            string agentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutonomousAgentInboxEntry>>(_inbox
                .Where(entry => entry.AgentId == agentId && entry.ConsumedUtc is null)
                .OrderBy(entry => entry.ReceivedUtc)
                .ToList());

        public Task MarkRepliesConsumedAsync(
            IEnumerable<string> entryIds,
            string consumedByRunId,
            DateTimeOffset consumedUtc,
            CancellationToken cancellationToken)
        {
            var ids = entryIds.ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < _inbox.Count; index++)
            {
                if (ids.Contains(_inbox[index].Id))
                {
                    _inbox[index] = _inbox[index] with
                    {
                        ConsumedUtc = consumedUtc,
                        ConsumedByRunId = consumedByRunId,
                    };
                }
            }

            return Task.CompletedTask;
        }

        public Task<AutonomousAgentContinuity?> GetContinuityAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_continuity.TryGetValue(agentId, out var continuity) ? continuity : null);

        public Task UpsertContinuityAsync(AutonomousAgentContinuity continuity, CancellationToken cancellationToken)
        {
            _continuity[continuity.AgentId] = continuity;
            return Task.CompletedTask;
        }
    }
}
