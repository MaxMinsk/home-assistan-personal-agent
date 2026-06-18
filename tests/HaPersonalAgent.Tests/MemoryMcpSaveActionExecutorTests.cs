using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaPersonalAgent.Tests;

public sealed class MemoryMcpSaveActionExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_upserts_a_home_fact_note_via_memory_mcp()
    {
        var client = new FakeMemoryMcpClient();
        var executor = new MemoryMcpSaveActionExecutor(client, NullLogger<MemoryMcpSaveActionExecutor>.Instance);
        var confirmation = CreateConfirmation(
            MemoryMcpSaveActionExecutor.MemoryMcpSaveActionKind,
            """{"statement":"The user prefers metric units","key":"units","tags":"profile,preferences"}""");

        var result = await executor.ExecuteAsync(confirmation, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("notes_upsert", client.LastToolName);
        Assert.NotNull(client.LastArguments);
        Assert.Equal("home", client.LastArguments!["domain"]);
        Assert.Equal("fact", client.LastArguments["type"]);
        Assert.Equal("The user prefers metric units", client.LastArguments["body"]);
        var dedupKey = Assert.IsType<string>(client.LastArguments["dedupKey"]);
        Assert.StartsWith("hpa-fact-telegram:1:2-", dedupKey);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_unsupported_action_kind()
    {
        var executor = new MemoryMcpSaveActionExecutor(new FakeMemoryMcpClient(), NullLogger<MemoryMcpSaveActionExecutor>.Instance);
        var confirmation = CreateConfirmation("something_else", """{"statement":"x"}""");

        var result = await executor.ExecuteAsync(confirmation, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_statement_missing()
    {
        var client = new FakeMemoryMcpClient();
        var executor = new MemoryMcpSaveActionExecutor(client, NullLogger<MemoryMcpSaveActionExecutor>.Instance);
        var confirmation = CreateConfirmation(MemoryMcpSaveActionExecutor.MemoryMcpSaveActionKind, """{"key":"x"}""");

        var result = await executor.ExecuteAsync(confirmation, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(client.LastToolName); // never reached the server
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_memory_mcp_reports_error()
    {
        var client = new FakeMemoryMcpClient { Result = new MemoryMcpToolResult(true, "schema rejected", null) };
        var executor = new MemoryMcpSaveActionExecutor(client, NullLogger<MemoryMcpSaveActionExecutor>.Instance);
        var confirmation = CreateConfirmation(
            MemoryMcpSaveActionExecutor.MemoryMcpSaveActionKind,
            """{"statement":"a durable fact"}""");

        var result = await executor.ExecuteAsync(confirmation, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    private static PendingConfirmation CreateConfirmation(string actionKind, string payloadJson) =>
        new(
            "conf-1",
            actionKind,
            "telegram:1:2",
            "2",
            "memory_save:telegram:1:2",
            payloadJson,
            "Save a durable fact",
            "Writes to shared Memory MCP",
            ConfirmationActionStatus.Executing,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(10),
            CompletedAtUtc: null,
            "corr-1",
            ResultJson: null,
            Error: null);

    private sealed class FakeMemoryMcpClient : IMemoryMcpClient
    {
        public string? LastToolName { get; private set; }

        public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }

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
            return Task.FromResult(Result);
        }
    }
}
