using HaPersonalAgent.Agent;
using HaPersonalAgent.Autonomous;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты сводного дайджеста (HPA-039, часть B) — заземлённый поиск связей и координатор доставки.
/// Зачем: при срабатывании нескольких агентов в одно окно должен уходить ОДИН дайджест, связи — только по факту,
/// а reply-якоря каждого агента — сохраняться в его запуск, чтобы ответы доезжали.
/// Как: фейковый рантайм для финдера; фейковые нотификатор/финдер + реальный SQLite-репозиторий для координатора.
/// </summary>
public class AutonomousDigestTests
{
    [Fact]
    public async Task Connection_finder_returns_empty_for_fewer_than_two_deliveries()
    {
        var finder = new AutonomousConnectionFinder(
            new FakeRuntime("""{ "connections": ["x"] }""", configured: true),
            NullLogger<AutonomousConnectionFinder>.Instance);

        Assert.Empty(await finder.FindConnectionsAsync(new[] { Delivery("A") }, CancellationToken.None));
    }

    [Fact]
    public async Task Connection_finder_parses_grounded_connections_from_the_model_json()
    {
        var runtime = new FakeRuntime(
            """Вот связи: { "connections": ["«A» и «B» оба про офис в центре"] } — всё.""",
            configured: true);
        var finder = new AutonomousConnectionFinder(runtime, NullLogger<AutonomousConnectionFinder>.Instance);

        var result = await finder.FindConnectionsAsync(new[] { Delivery("A"), Delivery("B") }, CancellationToken.None);

        Assert.Single(result);
        Assert.Contains("офис", result[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_finder_returns_empty_when_unparseable_or_unconfigured()
    {
        var unparseable = new AutonomousConnectionFinder(
            new FakeRuntime("нет тут никакого json", configured: true),
            NullLogger<AutonomousConnectionFinder>.Instance);
        Assert.Empty(await unparseable.FindConnectionsAsync(new[] { Delivery("A"), Delivery("B") }, CancellationToken.None));

        var unconfigured = new AutonomousConnectionFinder(
            new FakeRuntime("""{ "connections": ["x"] }""", configured: false),
            NullLogger<AutonomousConnectionFinder>.Instance);
        Assert.Empty(await unconfigured.FindConnectionsAsync(new[] { Delivery("A"), Delivery("B") }, CancellationToken.None));
    }

    [Fact]
    public async Task Single_result_is_delivered_as_a_normal_brief_not_a_digest()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var notifier = new FakeNotifier();
            var finder = new FakeFinder();
            var coordinator = new AutonomousDigestDelivery(
                repository, NullLogger<AutonomousDigestDelivery>.Instance, notifier, finder);

            var delivery = await SeedDeliveryAsync(repository, "Один");
            await coordinator.DeliverAsync(new[] { delivery }, CancellationToken.None);

            Assert.Equal(1, notifier.DeliverCalls);
            Assert.Equal(0, notifier.DigestCalls);
            Assert.Equal(0, finder.Calls);
            // Reply-якорь одиночного брифа сохранён в запуск.
            var run = await repository.GetRunAsync(delivery.Run.Id, CancellationToken.None);
            Assert.Equal("single-msg", run!.DeliveredMessageId);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Multiple_results_are_delivered_as_one_digest_with_connections_and_per_agent_anchors()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var notifier = new FakeNotifier();
            var finder = new FakeFinder { Connections = new[] { "«A» и «B» пересекаются по офису" } };
            var coordinator = new AutonomousDigestDelivery(
                repository, NullLogger<AutonomousDigestDelivery>.Instance, notifier, finder);

            var a = await SeedDeliveryAsync(repository, "A");
            var b = await SeedDeliveryAsync(repository, "B");
            await coordinator.DeliverAsync(new[] { a, b }, CancellationToken.None);

            Assert.Equal(0, notifier.DeliverCalls);
            Assert.Equal(1, notifier.DigestCalls);
            Assert.Equal(1, finder.Calls);
            Assert.Equal(new[] { "«A» и «B» пересекаются по офису" }, notifier.LastConnections);
            // Каждому агенту сохранён его собственный reply-якорь.
            Assert.Equal($"digest-{a.Run.Id}", (await repository.GetRunAsync(a.Run.Id, CancellationToken.None))!.DeliveredMessageId);
            Assert.Equal($"digest-{b.Run.Id}", (await repository.GetRunAsync(b.Run.Id, CancellationToken.None))!.DeliveredMessageId);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    private static async Task<AutonomousRunDelivery> SeedDeliveryAsync(
        SqliteAutonomousAgentRepository repository,
        string name)
    {
        var definition = AutonomousAgentDefinition.Create(name, "миссия", AutonomousAgentScheduleKind.Weekly);
        await repository.UpsertDefinitionAsync(definition, CancellationToken.None);
        var run = AutonomousAgentRun.Start(definition.Id);
        await repository.AppendRunAsync(run, CancellationToken.None);
        var output = new AutonomousRunOutput(
            $"сводка {name}",
            new[] { $"находка {name}" },
            Array.Empty<string>(),
            Array.Empty<string>(),
            null);
        return new AutonomousRunDelivery(definition, run, output, Array.Empty<AutonomousProposedAction>());
    }

    private static AutonomousRunDelivery Delivery(string name)
    {
        var definition = AutonomousAgentDefinition.Create(name, "миссия", AutonomousAgentScheduleKind.Weekly);
        var output = new AutonomousRunOutput(
            $"сводка {name}",
            new[] { $"находка {name}" },
            Array.Empty<string>(),
            Array.Empty<string>(),
            null);
        return new AutonomousRunDelivery(definition, AutonomousAgentRun.Start(definition.Id), output, Array.Empty<AutonomousProposedAction>());
    }

    private static SqliteAutonomousAgentRepository CreateRepository(string databasePath) =>
        new(new SqliteConnectionFactory(Options.Create(new AgentOptions { StateDatabasePath = databasePath })));

    private static string CreateTemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), "ha-personal-agent-tests", Guid.NewGuid().ToString("N"), "state.sqlite");

    private static void DeleteTemporaryDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeRuntime : IAgentRuntime
    {
        private readonly string _text;
        private readonly bool _configured;

        public FakeRuntime(string text, bool configured)
        {
            _text = text;
            _configured = configured;
        }

        public AgentRuntimeHealth GetHealth() => AgentRuntimeHealth.Configured(new LlmOptions());

        public Task<AgentRuntimeResponse> SendAsync(
            string message,
            AgentContext context,
            Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentRuntimeResponse(context.CorrelationId, _configured, _text, GetHealth()));
    }

    private sealed class FakeNotifier : IAutonomousAgentNotifier
    {
        public int DeliverCalls { get; private set; }
        public int DigestCalls { get; private set; }
        public IReadOnlyList<string>? LastConnections { get; private set; }

        public Task<string?> DeliverAsync(
            AutonomousAgentDefinition definition,
            AutonomousAgentRun run,
            AutonomousRunOutput output,
            IReadOnlyList<AutonomousProposedAction> proposedActions,
            CancellationToken cancellationToken)
        {
            DeliverCalls++;
            return Task.FromResult<string?>("single-msg");
        }

        public Task<IReadOnlyList<AutonomousDigestAnchor>> DeliverDigestAsync(
            IReadOnlyList<AutonomousRunDelivery> deliveries,
            IReadOnlyList<string> connections,
            CancellationToken cancellationToken)
        {
            DigestCalls++;
            LastConnections = connections;
            IReadOnlyList<AutonomousDigestAnchor> anchors = deliveries
                .Select(d => new AutonomousDigestAnchor(d.Run.Id, $"digest-{d.Run.Id}"))
                .ToList();
            return Task.FromResult(anchors);
        }
    }

    private sealed class FakeFinder : IAutonomousConnectionFinder
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> Connections { get; set; } = Array.Empty<string>();

        public Task<IReadOnlyList<string>> FindConnectionsAsync(
            IReadOnlyList<AutonomousRunDelivery> deliveries,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Connections);
        }
    }
}
