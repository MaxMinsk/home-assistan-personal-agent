using System.Text;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: builder prompt-а для persisted summary compaction шага.
/// Зачем: HAAG-055 требует стабильный и воспроизводимый merge-контракт для long-term summary без потери важных фактов.
/// Как: формирует строгую инструкцию по схеме delta-merge (old summary + tail summary) с anti-drift правилами и структурным markdown-форматом.
/// </summary>
public sealed class PersistedSummaryPromptBuilder
{
    public string Build(
        string? persistedSummary,
        string refreshReason,
        int messagesSincePersistedSummary)
    {
        var normalizedReason = PersistedSummaryRefreshReasons.Normalize(refreshReason);
        var hasBaselineSummary = !string.IsNullOrWhiteSpace(persistedSummary);
        var prompt = new StringBuilder(
            """
            Build persisted long-term conversation memory in Russian.
            This is not a short recap; it is durable memory for future runs.

            Merge algorithm (strict):
            1. Build tail summary from recent compacted dialogue.
            2. Merge it with existing summary baseline.
            3. Preserve previously known facts unless they are explicitly contradicted by new evidence.
            4. If conflict is detected, keep the new fact in the main section and move old/new mismatch to "## Конфликты и обновления".
            5. Output only final merged summary (no draft, no explanations).
            Canonical formula: new_summary = merge(old_summary, summary(new_tail)).

            Importance scoring:
            - Highest priority: names, numbers, dates, commitments, constraints, explicit preferences, accepted decisions.
            - Medium priority: active tasks and project states.
            - Low priority: general reflections without action or factual value.

            Exclude aggressively:
            - transient chatter, politeness formulas, jokes/emotional flavor, rhetorical questions;
            - one-off wording that does not carry lasting factual value;
            - direct quotes from dialogue and raw tool outputs.

            Return only this markdown structure:

            ## Контекст пользователя
            - ...

            ## Факты и решения
            - ...

            ## Открытые задачи
            - ...

            ## Ограничения и предпочтения
            - ...

            ## Конфликты и обновления
            - ...

            ## Source attribution
            - baseline_summary_used: yes|no
            - recent_tail_used: yes
            - source_notes: short references without raw quotes

            Format constraints:
            - 2-8 concise bullets for sections with data.
            - If no data for a section, exactly one bullet: "- нет данных".
            - No role labels, timestamps, message ids, tokens, secrets, or API/tool payload dumps.
            - No direct address to user, no questions.
            - Target 900-2200 characters, hard max 3200.
            """);

        prompt.AppendLine();
        prompt.AppendLine("Runtime diagnostics:");
        prompt.Append("- refresh_reason: ").AppendLine(normalizedReason);
        prompt.Append("- messages_since_previous_summary: ").AppendLine(messagesSincePersistedSummary.ToString(System.Globalization.CultureInfo.InvariantCulture));
        prompt.Append("- baseline_summary_present: ").AppendLine(hasBaselineSummary ? "yes" : "no");

        if (hasBaselineSummary)
        {
            prompt.AppendLine();
            prompt.AppendLine("Existing persisted summary baseline:");
            prompt.AppendLine("---");
            prompt.AppendLine(Truncate(persistedSummary!.Trim(), 2_500));
            prompt.AppendLine("---");
            prompt.AppendLine("Do not drop baseline facts only because latest dialogue topic changed.");
        }

        return prompt.ToString();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];
}
