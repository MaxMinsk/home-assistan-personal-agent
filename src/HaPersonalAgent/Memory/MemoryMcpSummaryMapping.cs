using System.Globalization;
using System.Text.Json;
using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Memory;

/// <summary>
/// What: maps a <see cref="ConversationSummaryMemory"/> to/from a Memory MCP `conversation_summary` note
/// (domain `home`), per the ha-personal-agent `memory-conventions` skill.
/// Why: HPA-004 mirrors the rolling summary to Memory MCP losslessly — the dedicated type holds the
/// metadata (version, source message id) the generic episode/fact types cannot.
/// How: <see cref="BuildUpsertArguments"/> produces the notes_upsert arguments; <see cref="TryParse"/>
/// rebuilds the record from a returned note so durable recall round-trips.
/// </summary>
public static class MemoryMcpSummaryMapping
{
    public const string Domain = "home";
    public const string NoteType = "conversation_summary";

    public static string BuildDedupKey(string conversationKey) => $"hpa-summary-{conversationKey}";

    public static IReadOnlyDictionary<string, object?> BuildUpsertArguments(
        ConversationSummaryMemory summary,
        string sourceAgent) =>
        new Dictionary<string, object?>
        {
            ["domain"] = Domain,
            ["type"] = NoteType,
            ["dedupKey"] = BuildDedupKey(summary.ConversationKey),
            ["title"] = $"Conversation summary — {summary.ConversationKey}",
            ["body"] = summary.Summary,
            ["sourceAgent"] = sourceAgent,
            ["tags"] = new[] { "ha-personal-agent", "summary" },
            ["payload"] = new Dictionary<string, object?>
            {
                ["conversation_key"] = summary.ConversationKey,
                ["summary"] = summary.Summary,
                ["summary_version"] = summary.SummaryVersion,
                ["source_last_message_id"] = summary.SourceLastMessageId,
                ["updated_utc"] = summary.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            },
        };

    /// <summary>Rebuild the record from a returned note's JSON (payload object or payloadJson string).</summary>
    public static bool TryParse(string? noteJson, out ConversationSummaryMemory? summary)
    {
        summary = null;
        if (string.IsNullOrWhiteSpace(noteJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(noteJson);
            if (!TryGetPayload(document.RootElement, out var payload))
            {
                return false;
            }

            if (!payload.TryGetProperty("conversation_key", out var keyElement)
                || !payload.TryGetProperty("summary", out var summaryElement))
            {
                return false;
            }

            var conversationKey = keyElement.GetString();
            var summaryText = summaryElement.GetString();
            if (string.IsNullOrEmpty(conversationKey) || summaryText is null)
            {
                return false;
            }

            var version = payload.TryGetProperty("summary_version", out var versionElement)
                && versionElement.TryGetInt32(out var parsedVersion)
                ? parsedVersion
                : 0;
            var sourceId = payload.TryGetProperty("source_last_message_id", out var sourceElement)
                && sourceElement.TryGetInt64(out var parsedSource)
                ? parsedSource
                : 0L;
            var updatedAt = payload.TryGetProperty("updated_utc", out var updatedElement)
                && DateTimeOffset.TryParse(
                    updatedElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedUpdated)
                ? parsedUpdated
                : default;

            summary = new ConversationSummaryMemory(conversationKey, summaryText, updatedAt, sourceId, version);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetPayload(JsonElement root, out JsonElement payload)
    {
        // The returned note carries its payload either as a nested "payload" object or a "payloadJson" string.
        if (root.TryGetProperty("payload", out payload) && payload.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (root.TryGetProperty("payloadJson", out var payloadJson)
            && payloadJson.ValueKind == JsonValueKind.String)
        {
            var raw = payloadJson.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var inner = JsonDocument.Parse(raw);
                payload = inner.RootElement.Clone();
                return payload.ValueKind == JsonValueKind.Object;
            }
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("conversation_key", out _))
        {
            payload = root;
            return true;
        }

        payload = default;
        return false;
    }
}
