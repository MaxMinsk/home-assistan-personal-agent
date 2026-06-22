using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Memory;

/// <summary>
/// What: one-time, idempotent backfill of existing local durable memory (conversation summaries) into
/// Memory MCP (HPA-010).
/// Why: when memory_store_type=memory_mcp, durable memory written before the live mirrors existed (or while
/// the store selector was sqlite) is invisible in Memory MCP; this seeds it once so prior memory is durable
/// and recallable there.
/// How: on startup (in the background, never blocking the host) it reuses the same notes_upsert mappings and
/// dedupKeys as the live mirrors, so re-running is safe. A persisted agent_state flag guards it to effectively
/// once; if everything fails (e.g. Memory MCP down) the flag is not set so the next boot retries. Best-effort:
/// it never throws out of ExecuteAsync.
/// </summary>
public sealed class MemoryMcpBackfillService : BackgroundService
{
    internal const string BackfillFlagKey = "memory_mcp_backfill_v1";
    internal const string BackfillFlagDoneValue = "done";

    private static readonly TimeSpan UpsertTimeout = TimeSpan.FromSeconds(5);

    private readonly AgentStateRepository _stateRepository;
    private readonly IMemoryMcpClient _memoryClient;
    private readonly IOptions<MemoryMcpOptions> _options;
    private readonly ILogger<MemoryMcpBackfillService> _logger;

    public MemoryMcpBackfillService(
        AgentStateRepository stateRepository,
        IMemoryMcpClient memoryClient,
        IOptions<MemoryMcpOptions> options,
        ILogger<MemoryMcpBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(stateRepository);
        ArgumentNullException.ThrowIfNull(memoryClient);
        ArgumentNullException.ThrowIfNull(options);

        _stateRepository = stateRepository;
        _memoryClient = memoryClient;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        RunBackfillAsync(stoppingToken);

    /// <summary>
    /// Runs the backfill once. Exposed for tests to drive directly without a host. Never throws.
    /// </summary>
    internal async Task RunBackfillAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = _options.Value;
            if (!string.Equals(options.StoreType, MemoryMcpOptions.StoreTypeMemoryMcp, StringComparison.OrdinalIgnoreCase)
                || !options.IsConfigured)
            {
                // Local SQLite remains the source of truth; only the memory_mcp store backfills forward.
                return;
            }

            var flag = await _stateRepository.GetAgentStateValueAsync(BackfillFlagKey, cancellationToken);
            if (string.Equals(flag, BackfillFlagDoneValue, StringComparison.Ordinal))
            {
                _logger.LogInformation("Memory MCP backfill already completed; skipping.");
                return;
            }

            var summaries = await _stateRepository.GetAllConversationSummariesAsync(cancellationToken);

            var summariesBackfilled = 0;
            var failures = 0;

            foreach (var summary in summaries)
            {
                var arguments = MemoryMcpSummaryMapping.BuildUpsertArguments(summary, ApplicationInfo.Name);
                if (await TryUpsertAsync(arguments, cancellationToken))
                {
                    summariesBackfilled++;
                }
                else
                {
                    failures++;
                }
            }

            var totalItems = summaries.Count;
            // If there was at least one item to copy and every one failed (e.g. Memory MCP is down), leave the
            // flag unset so the next boot retries. An empty local store is treated as a successful no-op backfill.
            var allFailed = totalItems > 0 && summariesBackfilled == 0;
            if (!allFailed)
            {
                await _stateRepository.SetAgentStateValueAsync(BackfillFlagKey, BackfillFlagDoneValue, cancellationToken);
            }

            _logger.LogInformation(
                "Memory MCP backfill completed: {SummariesBackfilled} summaries, {Failures} failures. Flag set: {FlagSet}.",
                summariesBackfilled,
                failures,
                !allFailed);
        }
        catch (Exception exception)
        {
            // Best-effort: a backfill failure (or Memory MCP outage) must never break startup.
            _logger.LogWarning(exception, "Memory MCP backfill failed; continuing without it.");
        }
    }

    private async Task<bool> TryUpsertAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(UpsertTimeout);

            var result = await _memoryClient.CallToolAsync("notes_upsert", arguments, timeout.Token);
            if (result.IsError)
            {
                _logger.LogWarning(
                    "Memory MCP rejected a backfill upsert for {DedupKey}: {Detail}",
                    arguments.TryGetValue("dedupKey", out var dedupKey) ? dedupKey : "(unknown)",
                    result.Text);
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Memory MCP backfill upsert failed for {DedupKey}; continuing on local memory.",
                arguments.TryGetValue("dedupKey", out var dedupKey) ? dedupKey : "(unknown)");
            return false;
        }
    }
}
