using HaPersonalAgent.Autonomous;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты дисциплины записи автономных агентов в Memory MCP (HPA-031).
/// Зачем: главное требование эпика — использовать общую память, но НЕ засорять её: заметок не должно становиться больше с каждым запуском.
/// Как: фейковый Memory MCP клиент фиксирует все notes_upsert; проверяем единственность капсулы, идемпотентный dedupKey и лимит фактов.
/// </summary>
public class AutonomousAgentCapsuleWriterTests
{
    [Fact]
    public async Task Repeated_runs_rewrite_one_capsule_instead_of_adding_notes()
    {
        var memory = new FakeMemoryMcpClient();
        var writer = CreateWriter(memory);
        var definition = CreateAgent(maxDurableFacts: 0);

        var first = await writer.PublishAsync(definition, CreateOutput(), CancellationToken.None);
        var second = await writer.PublishAsync(definition, CreateOutput(), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(AutonomousAgentCapsuleWriter.BuildCapsuleDedupKey(definition.Id), first);

        // Два запуска — два upsert'а, но по ОДНОМУ и тому же dedupKey, значит заметка одна.
        Assert.Equal(2, memory.Upserts.Count);
        Assert.All(memory.Upserts, upsert => Assert.Equal(first, upsert.DedupKey));
        Assert.Single(memory.Upserts.Select(upsert => upsert.DedupKey).Distinct(StringComparer.Ordinal));
        Assert.All(memory.Upserts, upsert =>
            Assert.Equal(AutonomousAgentCapsuleWriter.CapsuleNoteType, upsert.Type));
    }

    [Fact]
    public async Task Durable_facts_are_capped_by_the_agent_budget()
    {
        var memory = new FakeMemoryMcpClient();
        var writer = CreateWriter(memory);
        var definition = CreateAgent(maxDurableFacts: 2);
        var output = new AutonomousRunOutput(
            "сводка",
            Array.Empty<string>(),
            new[] { "факт 1", "факт 2", "факт 3", "факт 4" },
            "дальше");

        await writer.PublishAsync(definition, output, CancellationToken.None);

        var factUpserts = memory.Upserts
            .Where(upsert => upsert.Type == AutonomousAgentCapsuleWriter.FactNoteType)
            .ToList();

        Assert.Equal(2, factUpserts.Count);
        Assert.All(factUpserts, upsert => Assert.StartsWith($"hpa-agent-fact-{definition.Id}-", upsert.DedupKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_is_written_when_the_agent_may_not_write_memory()
    {
        var memory = new FakeMemoryMcpClient();
        var writer = CreateWriter(memory);
        var definition = CreateAgent(maxDurableFacts: 3) with
        {
            ToolScope = AutonomousAgentToolScope.Create(true, true, true, false, 3),
        };

        var capsuleKey = await writer.PublishAsync(definition, CreateOutput(), CancellationToken.None);

        Assert.Null(capsuleKey);
        Assert.Empty(memory.Upserts);
    }

    [Fact]
    public async Task Nothing_is_written_when_memory_mcp_is_not_configured()
    {
        var memory = new FakeMemoryMcpClient();
        var writer = CreateWriter(memory, configured: false);

        var capsuleKey = await writer.PublishAsync(CreateAgent(3), CreateOutput(), CancellationToken.None);

        Assert.Null(capsuleKey);
        Assert.Empty(memory.Upserts);
    }

    [Fact]
    public async Task Memory_failure_does_not_throw_and_reports_no_capsule()
    {
        var memory = new FakeMemoryMcpClient { ShouldFail = true };
        var writer = CreateWriter(memory);

        var capsuleKey = await writer.PublishAsync(CreateAgent(3), CreateOutput(), CancellationToken.None);

        Assert.Null(capsuleKey);
    }

    private static AutonomousAgentCapsuleWriter CreateWriter(
        IMemoryMcpClient memoryClient,
        bool configured = true) =>
        new(
            memoryClient,
            Options.Create(configured
                ? new MemoryMcpOptions { Endpoint = "https://memory.example/mcp", Token = "secret" }
                : new MemoryMcpOptions()),
            NullLogger<AutonomousAgentCapsuleWriter>.Instance);

    private static AutonomousAgentDefinition CreateAgent(int maxDurableFacts) =>
        AutonomousAgentDefinition.Create(
            "Бизнес в Минске",
            "Исследуй бизнес.",
            AutonomousAgentScheduleKind.Weekly,
            toolScope: AutonomousAgentToolScope.Create(true, true, true, true, maxDurableFacts));

    private static AutonomousRunOutput CreateOutput() =>
        new("Текущее состояние", new[] { "Вопрос?" }, Array.Empty<string>(), "следующий шаг");

    private sealed record UpsertCall(string Type, string DedupKey);

    private sealed class FakeMemoryMcpClient : IMemoryMcpClient
    {
        public List<UpsertCall> Upserts { get; } = new();

        public bool ShouldFail { get; init; }

        public Task<MemoryMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used in these tests.");

        public Task<MemoryMcpToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            if (ShouldFail)
            {
                return Task.FromResult(new MemoryMcpToolResult(true, "rejected", null));
            }

            Assert.Equal("notes_upsert", toolName);
            Assert.NotNull(arguments);
            Upserts.Add(new UpsertCall(
                arguments!["type"]?.ToString() ?? string.Empty,
                arguments["dedupKey"]?.ToString() ?? string.Empty));

            return Task.FromResult(new MemoryMcpToolResult(false, "{\"id\":\"note\"}", null));
        }
    }
}
