using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HaPersonalAgent.Tests;

public sealed class MemoryMcpConversationMemoryStoreTests
{
    [Fact]
    public void Mapping_builds_home_conversation_summary_upsert_arguments()
    {
        var summary = new ConversationSummaryMemory(
            "conv-1",
            "the rolling summary",
            DateTimeOffset.Parse("2026-06-18T10:00:00Z"),
            42,
            3);

        var args = MemoryMcpSummaryMapping.BuildUpsertArguments(summary, "ha-agent");

        Assert.Equal("home", args["domain"]);
        Assert.Equal("conversation_summary", args["type"]);
        Assert.Equal("hpa-summary-conv-1", args["dedupKey"]);
        Assert.Equal("ha-agent", args["sourceAgent"]);

        var payload = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(args["payload"]);
        Assert.Equal("conv-1", payload["conversation_key"]);
        Assert.Equal(3, payload["summary_version"]);
        Assert.Equal(42L, payload["source_last_message_id"]);
    }

    [Fact]
    public void Mapping_round_trips_from_payload_object()
    {
        var noteJson = """
            {"payload":{"conversation_key":"conv-1","summary":"hi","summary_version":3,"source_last_message_id":42,"updated_utc":"2026-06-18T10:00:00.0000000+00:00"}}
            """;

        Assert.True(MemoryMcpSummaryMapping.TryParse(noteJson, out var summary));
        Assert.NotNull(summary);
        Assert.Equal("conv-1", summary!.ConversationKey);
        Assert.Equal("hi", summary.Summary);
        Assert.Equal(3, summary.SummaryVersion);
        Assert.Equal(42L, summary.SourceLastMessageId);
    }

    [Fact]
    public void Mapping_round_trips_from_payload_json_string()
    {
        var noteJson = """
            {"id":"abc","payloadJson":"{\"conversation_key\":\"conv-9\",\"summary\":\"text\",\"summary_version\":7,\"source_last_message_id\":100,\"updated_utc\":\"2026-06-18T10:00:00.0000000+00:00\"}"}
            """;

        Assert.True(MemoryMcpSummaryMapping.TryParse(noteJson, out var summary));
        Assert.NotNull(summary);
        Assert.Equal("conv-9", summary!.ConversationKey);
        Assert.Equal(7, summary.SummaryVersion);
        Assert.Equal(100L, summary.SourceLastMessageId);
    }

    [Fact]
    public void Mapping_returns_false_on_unparseable_input()
    {
        Assert.False(MemoryMcpSummaryMapping.TryParse(null, out _));
        Assert.False(MemoryMcpSummaryMapping.TryParse("not json", out _));
        Assert.False(MemoryMcpSummaryMapping.TryParse("""{"payload":{"summary":"no key"}}""", out _));
    }

    [Fact]
    public async Task Upsert_writes_local_and_mirrors_to_memory_mcp()
    {
        var dbPath = CreateTemporaryDatabasePath();
        try
        {
            var inner = new SqliteConversationMemoryStore(await CreateRepositoryAsync(dbPath));
            var client = new FakeMemoryMcpClient();
            var store = CreateStore(inner, client);
            var summary = new ConversationSummaryMemory("conv-1", "hi", DateTimeOffset.UtcNow, 5, 2);

            await store.UpsertConversationSummaryAsync(summary, CancellationToken.None);

            var local = await inner.GetConversationSummaryAsync("conv-1", CancellationToken.None);
            Assert.NotNull(local);
            Assert.Equal("notes_upsert", client.LastToolName);
            Assert.NotNull(client.LastArguments);
            Assert.Equal("conversation_summary", client.LastArguments!["type"]);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(dbPath);
        }
    }

    [Fact]
    public async Task Upsert_succeeds_when_memory_mcp_mirror_throws()
    {
        var dbPath = CreateTemporaryDatabasePath();
        try
        {
            var inner = new SqliteConversationMemoryStore(await CreateRepositoryAsync(dbPath));
            var client = new FakeMemoryMcpClient { ThrowOnCall = true };
            var store = CreateStore(inner, client);
            var summary = new ConversationSummaryMemory("conv-1", "hi", DateTimeOffset.UtcNow, 5, 2);

            await store.UpsertConversationSummaryAsync(summary, CancellationToken.None);

            // The local write still succeeds even though the MCP mirror failed (hot path is never broken).
            var local = await inner.GetConversationSummaryAsync("conv-1", CancellationToken.None);
            Assert.NotNull(local);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(dbPath);
        }
    }

    [Fact]
    public async Task Get_summary_reads_local_without_calling_memory_mcp()
    {
        var dbPath = CreateTemporaryDatabasePath();
        try
        {
            var inner = new SqliteConversationMemoryStore(await CreateRepositoryAsync(dbPath));
            await inner.UpsertConversationSummaryAsync(
                new ConversationSummaryMemory("conv-1", "hi", DateTimeOffset.UtcNow, 5, 2),
                CancellationToken.None);
            var client = new FakeMemoryMcpClient();
            var store = CreateStore(inner, client);

            var summary = await store.GetConversationSummaryAsync("conv-1", CancellationToken.None);

            Assert.NotNull(summary);
            Assert.Null(client.LastToolName); // no remote call on the hot read path
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(dbPath);
        }
    }

    private static MemoryMcpConversationMemoryStore CreateStore(IConversationMemoryStore inner, IMemoryMcpClient client) =>
        new(
            inner,
            client,
            Options.Create(new MemoryMcpOptions
            {
                Endpoint = "https://memory.kazmin.tech/mcp",
                Token = "t",
                StoreType = MemoryMcpOptions.StoreTypeMemoryMcp,
            }),
            NullLogger<MemoryMcpConversationMemoryStore>.Instance);

    private static async Task<AgentStateRepository> CreateRepositoryAsync(string databasePath)
    {
        var repository = new AgentStateRepository(
            new SqliteConnectionFactory(Options.Create(new AgentOptions { StateDatabasePath = databasePath })));
        await repository.InitializeAsync(CancellationToken.None);
        return repository;
    }

    private static string CreateTemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), "ha-mcp-store-tests", Guid.NewGuid().ToString("N"), "state.sqlite");

    private static void DeleteTemporaryDatabaseDirectory(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeMemoryMcpClient : IMemoryMcpClient
    {
        public string? LastToolName { get; private set; }

        public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }

        public bool ThrowOnCall { get; set; }

        public MemoryMcpToolResult Result { get; set; } = new(false, "ok", null);

        public Task<MemoryMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult(MemoryMcpDiscoveryResult.Reachable("https://memory.kazmin.tech/mcp", "0.48.0", 49));

        public Task<MemoryMcpToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            LastToolName = toolName;
            LastArguments = arguments;
            if (ThrowOnCall)
            {
                throw new InvalidOperationException("Memory MCP is down.");
            }

            return Task.FromResult(Result);
        }
    }
}
