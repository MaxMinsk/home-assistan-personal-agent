using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HaPersonalAgent.Tests;

/// <summary>
/// What: tests for the HPA-010 one-time backfill of local durable memory into Memory MCP.
/// Why: the backfill must stay a no-op unless memory_mcp is selected AND configured, run effectively once
/// (guarded by a persisted flag), copy every local summary and capsule via notes_upsert, and never break
/// startup — including not setting the flag when every upsert fails so the next boot retries.
/// How: drives the internal RunBackfillAsync directly against a real SQLite-backed repository and a fake
/// IMemoryMcpClient across the store-selector, already-done, success, and all-failed cases.
/// </summary>
public sealed class MemoryMcpBackfillServiceTests
{
    [Fact]
    public async Task RunBackfillAsync_is_a_no_op_when_store_type_is_default_sqlite()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            await SeedLocalMemoryAsync(repository);
            var client = new FakeMemoryMcpClient();
            var service = CreateService(repository, client, new MemoryMcpOptions
            {
                // Configured endpoint/token, but the default sqlite store selector still short-circuits.
                Endpoint = "https://memory.kazmin.tech/mcp",
                Token = "secret",
            });

            await service.RunBackfillAsync(CancellationToken.None);

            Assert.Equal(0, client.CallCount);
            Assert.Null(await repository.GetAgentStateValueAsync(MemoryMcpBackfillService.BackfillFlagKey, CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task RunBackfillAsync_is_a_no_op_when_memory_mcp_selected_but_not_configured()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            await SeedLocalMemoryAsync(repository);
            var client = new FakeMemoryMcpClient();
            var service = CreateService(repository, client, new MemoryMcpOptions
            {
                StoreType = MemoryMcpOptions.StoreTypeMemoryMcp,
                // No endpoint/token => not configured.
            });

            await service.RunBackfillAsync(CancellationToken.None);

            Assert.Equal(0, client.CallCount);
            Assert.Null(await repository.GetAgentStateValueAsync(MemoryMcpBackfillService.BackfillFlagKey, CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task RunBackfillAsync_is_a_no_op_when_flag_already_done()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            await SeedLocalMemoryAsync(repository);
            await repository.SetAgentStateValueAsync(
                MemoryMcpBackfillService.BackfillFlagKey,
                MemoryMcpBackfillService.BackfillFlagDoneValue,
                CancellationToken.None);
            var client = new FakeMemoryMcpClient();
            var service = CreateService(repository, client, ConfiguredMemoryMcpOptions());

            await service.RunBackfillAsync(CancellationToken.None);

            Assert.Equal(0, client.CallCount);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task RunBackfillAsync_upserts_each_local_summary_and_capsule_and_sets_flag()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            await SeedLocalMemoryAsync(repository);
            var client = new FakeMemoryMcpClient();
            var service = CreateService(repository, client, ConfiguredMemoryMcpOptions());

            await service.RunBackfillAsync(CancellationToken.None);

            // Two summaries + two capsules seeded => four notes_upsert calls.
            Assert.Equal(4, client.CallCount);
            Assert.All(client.ToolNames, toolName => Assert.Equal("notes_upsert", toolName));
            Assert.Contains("hpa-summary-telegram:1:2", client.DedupKeys);
            Assert.Contains("hpa-summary-telegram:3:4", client.DedupKeys);
            Assert.Contains("hpa-capsule-telegram:1:2-dog", client.DedupKeys);
            Assert.Contains("hpa-capsule-telegram:3:4-house", client.DedupKeys);
            Assert.Equal(
                MemoryMcpBackfillService.BackfillFlagDoneValue,
                await repository.GetAgentStateValueAsync(MemoryMcpBackfillService.BackfillFlagKey, CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task RunBackfillAsync_never_throws_and_does_not_set_flag_when_every_upsert_fails()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            await SeedLocalMemoryAsync(repository);
            var client = new FakeMemoryMcpClient { ShouldThrow = true };
            var service = CreateService(repository, client, ConfiguredMemoryMcpOptions());

            // A Memory MCP outage must never break startup.
            await service.RunBackfillAsync(CancellationToken.None);

            // Flag stays unset so the next boot retries the backfill.
            Assert.Null(await repository.GetAgentStateValueAsync(MemoryMcpBackfillService.BackfillFlagKey, CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    private static MemoryMcpBackfillService CreateService(
        AgentStateRepository repository,
        IMemoryMcpClient client,
        MemoryMcpOptions options) =>
        new(repository, client, Options.Create(options), NullLogger<MemoryMcpBackfillService>.Instance);

    private static MemoryMcpOptions ConfiguredMemoryMcpOptions() =>
        new()
        {
            StoreType = MemoryMcpOptions.StoreTypeMemoryMcp,
            Endpoint = "https://memory.kazmin.tech/mcp",
            Token = "secret",
        };

    private static async Task SeedLocalMemoryAsync(AgentStateRepository repository)
    {
        var now = new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero);
        await repository.UpsertConversationSummaryAsync(
            new ConversationSummaryMemory("telegram:1:2", "summary-a", now, SourceLastMessageId: 5, SummaryVersion: 1),
            CancellationToken.None);
        await repository.UpsertConversationSummaryAsync(
            new ConversationSummaryMemory("telegram:3:4", "summary-b", now, SourceLastMessageId: 9, SummaryVersion: 2),
            CancellationToken.None);
        await repository.UpsertProjectCapsulesAsync(
            new[]
            {
                new ProjectCapsuleMemory("telegram:1:2", "dog", "Щенок", "## Факты", "conversation", 0.8d, 5L, now, 1),
                new ProjectCapsuleMemory("telegram:3:4", "house", "Стройка", "## Status", "conversation", 0.9d, 7L, now, 2),
            },
            CancellationToken.None);
    }

    private static AgentStateRepository CreateRepository(string databasePath)
    {
        var options = Options.Create(new AgentOptions
        {
            StateDatabasePath = databasePath,
        });
        var connectionFactory = new SqliteConnectionFactory(options);

        return new AgentStateRepository(connectionFactory);
    }

    private static string CreateTemporaryDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            "ha-personal-agent-tests",
            Guid.NewGuid().ToString("N"),
            "state.sqlite");

    private static void DeleteTemporaryDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeMemoryMcpClient : IMemoryMcpClient
    {
        public int CallCount { get; private set; }

        public List<string> ToolNames { get; } = new();

        public List<string?> DedupKeys { get; } = new();

        public bool ShouldThrow { get; set; }

        public MemoryMcpToolResult Result { get; set; } = new(false, "ok", null);

        public Task<MemoryMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult(MemoryMcpDiscoveryResult.Reachable("https://memory.kazmin.tech/mcp", "0.48.0", 49));

        public Task<MemoryMcpToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Memory MCP is unreachable.");
            }

            CallCount++;
            ToolNames.Add(toolName);
            DedupKeys.Add(arguments is not null && arguments.TryGetValue("dedupKey", out var dedupKey)
                ? dedupKey as string
                : null);
            return Task.FromResult(Result);
        }
    }
}
