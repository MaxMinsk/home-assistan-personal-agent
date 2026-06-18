using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// What: builds a no-op <see cref="MemoryMcpCapsuleMirror"/> for tests that construct capsule write paths.
/// Why: HPA-011 added the mirror as a constructor dependency; with the default sqlite store selector the
/// mirror short-circuits, so no Memory MCP call is ever made and tests stay offline and deterministic.
/// How: wraps a throwing fake client behind the default <see cref="MemoryMcpOptions"/> (StoreType=sqlite),
/// which the mirror treats as a no-op before it ever touches the client.
/// </summary>
internal static class TestCapsuleMirror
{
    public static MemoryMcpCapsuleMirror CreateNoOp() =>
        new(
            new ThrowingMemoryMcpClient(),
            Options.Create(new MemoryMcpOptions()),
            NullLogger<MemoryMcpCapsuleMirror>.Instance);

    private sealed class ThrowingMemoryMcpClient : IMemoryMcpClient
    {
        public Task<MemoryMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The no-op capsule mirror must never reach Memory MCP.");

        public Task<MemoryMcpToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The no-op capsule mirror must never reach Memory MCP.");
    }
}
