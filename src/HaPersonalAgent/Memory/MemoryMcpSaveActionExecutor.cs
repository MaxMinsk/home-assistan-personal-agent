using System.Globalization;
using System.Text;
using System.Text.Json;
using HaPersonalAgent.Confirmation;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Memory;

/// <summary>
/// What: confirmation executor that persists an agent-proposed durable fact to Memory MCP (HPA-009).
/// Why: the agent's propose_memory_save tool only PROPOSES; the actual notes_upsert runs here only after
/// the user approves (/approve), so the agent can never write to shared memory unattended.
/// How: parses the proposed payload and maps it to a `fact` note in domain `home` (per the
/// memory-conventions skill), then calls notes_upsert via <see cref="IMemoryMcpClient"/>. Idempotent by a
/// conversation-scoped dedupKey.
/// </summary>
public sealed class MemoryMcpSaveActionExecutor : IConfirmationActionExecutor
{
    public const string MemoryMcpSaveActionKind = "memory_mcp_save";
    public const string MemoryDomain = "home";
    public const string NoteType = "fact";

    private const int MaxStatementLength = 600;
    private const int MaxSlugLength = 48;

    private readonly IMemoryMcpClient _memoryClient;
    private readonly ILogger<MemoryMcpSaveActionExecutor> _logger;

    public MemoryMcpSaveActionExecutor(IMemoryMcpClient memoryClient, ILogger<MemoryMcpSaveActionExecutor> logger)
    {
        _memoryClient = memoryClient;
        _logger = logger;
    }

    public string ActionKind => MemoryMcpSaveActionKind;

    public async Task<ConfirmationActionExecutionResult> ExecuteAsync(
        PendingConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        if (!string.Equals(confirmation.ActionKind, MemoryMcpSaveActionKind, StringComparison.Ordinal))
        {
            return ConfirmationActionExecutionResult.Failure($"Unsupported action kind '{confirmation.ActionKind}'.");
        }

        if (!TryParsePayload(confirmation.PayloadJson, out var statement, out var key, out var tags, out var error))
        {
            return ConfirmationActionExecutionResult.Failure(error ?? "Invalid memory_save payload.");
        }

        var conversationKey = string.IsNullOrWhiteSpace(confirmation.ConversationKey) ? "global" : confirmation.ConversationKey;
        var slug = BuildSlug(string.IsNullOrWhiteSpace(key) ? statement : key);
        var dedupKey = $"hpa-fact-{conversationKey}-{slug}";

        var noteTags = new List<string> { "ha-personal-agent", "profile" };
        noteTags.AddRange(tags);

        try
        {
            var result = await _memoryClient.CallToolAsync(
                "notes_upsert",
                new Dictionary<string, object?>
                {
                    ["domain"] = MemoryDomain,
                    ["type"] = NoteType,
                    ["dedupKey"] = dedupKey,
                    ["title"] = Truncate(statement, 80),
                    ["body"] = statement,
                    ["sourceAgent"] = ApplicationInfo.Name,
                    ["tags"] = noteTags.ToArray(),
                    ["payload"] = new Dictionary<string, object?>
                    {
                        ["statement"] = statement,
                        ["source"] = "ha-personal-agent (conversation)",
                        ["as_of"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    },
                },
                cancellationToken);

            if (result.IsError)
            {
                _logger.LogWarning(
                    "Memory MCP rejected memory_save confirmation {ConfirmationId}: {Detail}",
                    confirmation.Id,
                    result.Text);
                return ConfirmationActionExecutionResult.Failure("Memory MCP rejected the memory save.");
            }

            var resultJson = JsonSerializer.Serialize(new
            {
                action = MemoryMcpSaveActionKind,
                domain = MemoryDomain,
                type = NoteType,
                dedupKey,
                saved = true,
            });

            _logger.LogInformation(
                "Memory MCP save confirmation {ConfirmationId} completed; dedupKey {DedupKey}.",
                confirmation.Id,
                dedupKey);
            return ConfirmationActionExecutionResult.Success(resultJson);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Memory MCP save confirmation {ConfirmationId} failed.", confirmation.Id);
            return ConfirmationActionExecutionResult.Failure($"Memory save failed: {exception.GetType().Name}.");
        }
    }

    private static bool TryParsePayload(
        string payloadJson,
        out string statement,
        out string key,
        out IReadOnlyList<string> tags,
        out string? error)
    {
        statement = string.Empty;
        key = string.Empty;
        tags = Array.Empty<string>();
        error = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            error = "Payload is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            statement = root.TryGetProperty("statement", out var statementElement)
                ? (statementElement.GetString() ?? string.Empty).Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(statement))
            {
                error = "statement must be non-empty.";
                return false;
            }

            if (statement.Length > MaxStatementLength)
            {
                statement = statement[..MaxStatementLength];
            }

            key = root.TryGetProperty("key", out var keyElement)
                ? (keyElement.GetString() ?? string.Empty).Trim()
                : string.Empty;

            if (root.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.String)
            {
                var raw = tagsElement.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    tags = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }
            }

            return true;
        }
        catch (JsonException)
        {
            error = "Payload is not valid JSON.";
            return false;
        }
    }

    private static string BuildSlug(string value)
    {
        var builder = new StringBuilder(MaxSlugLength);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if ((character is ' ' or '-' or '_') && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= MaxSlugLength)
            {
                break;
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "note" : slug;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
