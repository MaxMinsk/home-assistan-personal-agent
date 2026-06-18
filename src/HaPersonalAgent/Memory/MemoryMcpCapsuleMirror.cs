using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Memory;

/// <summary>
/// What: best-effort write-through mirror of project capsules to Memory MCP (HPA-011).
/// Why: when memory_store_type=memory_mcp, every capsule upsert should also land in Memory MCP as a lossless
/// `project_capsule` note (domain home) so derived project memory is durable and visible there.
/// How: after the local SQLite upsert, callers invoke <see cref="MirrorAsync"/>; it no-ops unless the store
/// selector is memory_mcp AND the client is configured, then mirrors each capsule via notes_upsert under a
/// short timeout and never throws — a Memory MCP outage must never break the turn.
/// </summary>
public sealed class MemoryMcpCapsuleMirror
{
    private static readonly TimeSpan MirrorTimeout = TimeSpan.FromSeconds(5);

    private readonly IMemoryMcpClient _memoryClient;
    private readonly IOptions<MemoryMcpOptions> _options;
    private readonly ILogger<MemoryMcpCapsuleMirror> _logger;

    public MemoryMcpCapsuleMirror(
        IMemoryMcpClient memoryClient,
        IOptions<MemoryMcpOptions> options,
        ILogger<MemoryMcpCapsuleMirror> logger)
    {
        ArgumentNullException.ThrowIfNull(memoryClient);
        ArgumentNullException.ThrowIfNull(options);

        _memoryClient = memoryClient;
        _options = options;
        _logger = logger;
    }

    public async Task MirrorAsync(IEnumerable<ProjectCapsuleMemory> capsules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capsules);

        var options = _options.Value;
        if (!string.Equals(options.StoreType, MemoryMcpOptions.StoreTypeMemoryMcp, StringComparison.OrdinalIgnoreCase)
            || !options.IsConfigured)
        {
            // Local SQLite remains the source of truth; only the memory_mcp store mirrors forward.
            return;
        }

        foreach (var capsule in capsules)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(MirrorTimeout);

                var arguments = MemoryMcpCapsuleMapping.BuildUpsertArguments(capsule, ApplicationInfo.Name);
                var result = await _memoryClient.CallToolAsync("notes_upsert", arguments, timeout.Token);
                if (result.IsError)
                {
                    _logger.LogWarning(
                        "Memory MCP rejected the project capsule mirror for {ConversationKey}/{CapsuleKey}: {Detail}",
                        capsule.ConversationKey,
                        capsule.CapsuleKey,
                        result.Text);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Memory MCP project capsule mirror failed for {ConversationKey}/{CapsuleKey}; continuing on local memory.",
                    capsule.ConversationKey,
                    capsule.CapsuleKey);
            }
        }
    }
}
