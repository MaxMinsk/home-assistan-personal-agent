using System.Globalization;
using HaPersonalAgent.Storage;

namespace HaPersonalAgent.Memory;

/// <summary>
/// What: maps a <see cref="ProjectCapsuleMemory"/> to a Memory MCP `project_capsule` note (domain `home`),
/// per the ha-personal-agent `memory-conventions` skill.
/// Why: HPA-011 mirrors project capsules to Memory MCP losslessly — the dedicated type holds the structured
/// metadata (scope, confidence, source event id, version) the generic episode/fact types cannot.
/// How: <see cref="BuildUpsertArguments"/> produces the notes_upsert arguments with a conversation+capsule
/// scoped dedupKey so repeated upserts of the same capsule edit the same note idempotently.
/// </summary>
public static class MemoryMcpCapsuleMapping
{
    public const string Domain = "home";
    public const string NoteType = "project_capsule";

    public static string BuildDedupKey(string conversationKey, string capsuleKey) =>
        $"hpa-capsule-{conversationKey}-{capsuleKey}";

    public static IReadOnlyDictionary<string, object?> BuildUpsertArguments(
        ProjectCapsuleMemory capsule,
        string sourceAgent) =>
        new Dictionary<string, object?>
        {
            ["domain"] = Domain,
            ["type"] = NoteType,
            ["dedupKey"] = BuildDedupKey(capsule.ConversationKey, capsule.CapsuleKey),
            ["title"] = capsule.Title,
            ["body"] = capsule.ContentMarkdown,
            ["sourceAgent"] = sourceAgent,
            ["tags"] = new[] { "ha-personal-agent", "capsule" },
            ["payload"] = new Dictionary<string, object?>
            {
                ["conversation_key"] = capsule.ConversationKey,
                ["capsule_key"] = capsule.CapsuleKey,
                ["title"] = capsule.Title,
                ["content_markdown"] = capsule.ContentMarkdown,
                ["scope"] = capsule.Scope,
                ["confidence"] = capsule.Confidence,
                ["source_event_id"] = capsule.SourceEventId,
                ["version"] = capsule.Version,
                ["updated_utc"] = capsule.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            },
        };
}
