using System.Text;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: сборка входного сообщения одного фонового запуска агента.
/// Зачем: качество автономного исследования определяется тем, что агент помнит с прошлого раза и какие ответы пользователя пришли с тех пор.
/// Как: чистая функция из миссии, состояния непрерывности, накопленных ответов и прошлой сводки; в конце — жёсткий контракт формата вывода.
/// </summary>
public static class AutonomousAgentPromptBuilder
{
    public const int MaxQuestionsPerRun = 3;

    public static string BuildRunInput(
        AutonomousAgentDefinition definition,
        AutonomousAgentContinuity? continuity,
        IReadOnlyList<AutonomousAgentInboxEntry> pendingReplies,
        string? previousSummary)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(pendingReplies);

        var builder = new StringBuilder();

        builder.AppendLine("You are running as an autonomous background research agent. The user is NOT present during this run.");
        builder.AppendLine("Do the research now, then report. Never ask the user to confirm anything mid-run: put every open question into the `questions` field of your answer instead.");
        builder.AppendLine();

        builder.AppendLine("## Mission");
        builder.AppendLine(definition.Mission);
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            builder.AppendLine("## What you reported last time");
            builder.AppendLine(previousSummary.Trim());
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(continuity?.Focus))
        {
            builder.AppendLine("## Where you left off (focus for this run)");
            builder.AppendLine(continuity!.Focus!.Trim());
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(continuity?.OpenQuestions))
        {
            builder.AppendLine("## Questions you asked earlier and were still open");
            builder.AppendLine(continuity!.OpenQuestions!.Trim());
            builder.AppendLine();
        }

        var answers = pendingReplies
            .Where(reply => reply.Source != AutonomousAgentReplySource.Conversation)
            .ToList();
        var chatContext = pendingReplies
            .Where(reply => reply.Source == AutonomousAgentReplySource.Conversation)
            .ToList();

        if (answers.Count > 0)
        {
            builder.AppendLine("## NEW answers from the user since your last run (authoritative — follow them)");
            var index = 1;
            foreach (var reply in answers)
            {
                builder.AppendLine($"{index}. {reply.Text}");
                index++;
            }

            builder.AppendLine();
            builder.AppendLine("Treat these answers as decisions: fold them into the research and stop re-asking what they already settled.");
            builder.AppendLine();
        }

        if (chatContext.Count > 0)
        {
            builder.AppendLine("## New relevant context the user mentioned in chat since your last run");
            var index = 1;
            foreach (var note in chatContext)
            {
                builder.AppendLine($"{index}. {note.Text}");
                index++;
            }

            builder.AppendLine();
            builder.AppendLine("This is context the user brought up in conversation, not a direct answer to your questions; weigh it and fold in what is relevant to the mission.");
            builder.AppendLine();
        }

        builder.AppendLine("## Rules for this run");
        builder.AppendLine("- Ground every claim in something you actually retrieved with a tool. If a tool returned nothing, say so plainly — never invent facts, numbers, sources or listings.");
        builder.AppendLine("- This is a read-only run: you cannot control devices and cannot write to long-term memory yourself.");
        builder.AppendLine($"- Ask at most {MaxQuestionsPerRun} questions, and only ones that would genuinely change your next step.");
        builder.AppendLine();

        builder.AppendLine("## Answer format (respond with ONLY this JSON object)");
        builder.AppendLine("""
            {
              "summary": "one or two sentences framing what this run covered",
              "findings": ["3-5 findings, most important first; each a single scannable line: thesis + one-line support"],
              "questions": ["clarifying question 1", "clarifying question 2"],
              "durableFacts": ["a durable fact worth remembering long-term, only if genuinely reusable"],
              "nextFocus": "one sentence: what you will work on next run"
            }
            """);
        builder.AppendLine();
        builder.AppendLine($"Keep `durableFacts` to at most {definition.ToolScope.MaxDurableFactsPerRun} item(s); leave the array empty when nothing is worth persisting.");

        return builder.ToString();
    }
}
