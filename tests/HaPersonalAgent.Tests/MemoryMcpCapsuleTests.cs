using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HaPersonalAgent.Tests;

/// <summary>
/// What: tests for the HPA-011 project-capsule mirror to Memory MCP.
/// Why: the mapping must round-trip every capsule field to the snake_case `project_capsule` schema, and the
/// mirror must stay a no-op unless memory_mcp is selected AND configured, while never breaking the turn.
/// How: exercises the mapping directly, then drives the mirror with a fake client across the store-selector
/// and failure cases.
/// </summary>
public sealed class MemoryMcpCapsuleTests
{
    [Fact]
    public void BuildUpsertArguments_maps_capsule_to_home_project_capsule_note()
    {
        var capsule = CreateCapsule();

        var arguments = MemoryMcpCapsuleMapping.BuildUpsertArguments(capsule, "Home Assistant Personal Agent");

        Assert.Equal("home", arguments["domain"]);
        Assert.Equal("project_capsule", arguments["type"]);
        Assert.Equal("hpa-capsule-telegram:1:2-house_build", arguments["dedupKey"]);
        Assert.Equal(capsule.Title, arguments["title"]);
        Assert.Equal(capsule.ContentMarkdown, arguments["body"]);
        Assert.Equal("Home Assistant Personal Agent", arguments["sourceAgent"]);

        var tags = Assert.IsType<string[]>(arguments["tags"]);
        Assert.Equal(new[] { "ha-personal-agent", "capsule" }, tags);

        var payload = Assert.IsType<Dictionary<string, object?>>(arguments["payload"]);
        Assert.Equal("telegram:1:2", payload["conversation_key"]);
        Assert.Equal("house_build", payload["capsule_key"]);
        Assert.Equal(capsule.Title, payload["title"]);
        Assert.Equal(capsule.ContentMarkdown, payload["content_markdown"]);
        Assert.Equal("conversation", payload["scope"]);
        Assert.Equal(0.87d, payload["confidence"]);
        Assert.Equal(42L, payload["source_event_id"]);
        Assert.Equal(3, payload["version"]);
        Assert.Equal(
            capsule.UpdatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            payload["updated_utc"]);
    }

    [Fact]
    public async Task MirrorAsync_is_a_no_op_when_store_type_is_default_sqlite()
    {
        var client = new FakeMemoryMcpClient();
        var mirror = CreateMirror(client, new MemoryMcpOptions
        {
            // Configured endpoint/token, but the default sqlite store selector still short-circuits.
            Endpoint = "https://memory.kazmin.tech/mcp",
            Token = "secret",
        });

        await mirror.MirrorAsync(new[] { CreateCapsule() }, CancellationToken.None);

        Assert.Null(client.LastToolName);
    }

    [Fact]
    public async Task MirrorAsync_is_a_no_op_when_memory_mcp_selected_but_not_configured()
    {
        var client = new FakeMemoryMcpClient();
        var mirror = CreateMirror(client, new MemoryMcpOptions
        {
            StoreType = MemoryMcpOptions.StoreTypeMemoryMcp,
            // No endpoint/token => not configured.
        });

        await mirror.MirrorAsync(new[] { CreateCapsule() }, CancellationToken.None);

        Assert.Null(client.LastToolName);
    }

    [Fact]
    public async Task MirrorAsync_upserts_each_capsule_when_memory_mcp_selected_and_configured()
    {
        var client = new FakeMemoryMcpClient();
        var mirror = CreateMirror(client, new MemoryMcpOptions
        {
            StoreType = MemoryMcpOptions.StoreTypeMemoryMcp,
            Endpoint = "https://memory.kazmin.tech/mcp",
            Token = "secret",
        });

        await mirror.MirrorAsync(new[] { CreateCapsule() }, CancellationToken.None);

        Assert.Equal("notes_upsert", client.LastToolName);
        Assert.NotNull(client.LastArguments);
        Assert.Equal("project_capsule", client.LastArguments!["type"]);
        Assert.Equal("hpa-capsule-telegram:1:2-house_build", client.LastArguments["dedupKey"]);
    }

    [Fact]
    public async Task MirrorAsync_never_throws_when_the_client_throws()
    {
        var client = new FakeMemoryMcpClient { ShouldThrow = true };
        var mirror = CreateMirror(client, new MemoryMcpOptions
        {
            StoreType = MemoryMcpOptions.StoreTypeMemoryMcp,
            Endpoint = "https://memory.kazmin.tech/mcp",
            Token = "secret",
        });

        // A Memory MCP outage must never break the turn.
        await mirror.MirrorAsync(new[] { CreateCapsule() }, CancellationToken.None);
    }

    private static MemoryMcpCapsuleMirror CreateMirror(IMemoryMcpClient client, MemoryMcpOptions options) =>
        new(client, Options.Create(options), NullLogger<MemoryMcpCapsuleMirror>.Instance);

    private static ProjectCapsuleMemory CreateCapsule() =>
        new(
            "telegram:1:2",
            "house_build",
            "Стройка дома",
            "## Status\n- foundation poured\n- next: framing",
            "conversation",
            0.87d,
            42L,
            new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero),
            3);

    private sealed class FakeMemoryMcpClient : IMemoryMcpClient
    {
        public string? LastToolName { get; private set; }

        public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }

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

            LastToolName = toolName;
            LastArguments = arguments;
            return Task.FromResult(Result);
        }
    }
}
