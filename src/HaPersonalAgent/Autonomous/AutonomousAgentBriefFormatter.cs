using System.Text;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: форматирование сводки автономного агента в сообщение и его нарезка под лимит Telegram.
/// Зачем: пользователь читает бриф, а не сырой JSON; при этом длинный ответ нельзя молча обрезать — он должен дойти целиком.
/// Как: чистые функции (легко тестируются): собирает заголовок + сводку + пронумерованные вопросы + подсказку про reply, затем режет по границам абзацев/строк.
/// </summary>
public static class AutonomousAgentBriefFormatter
{
    /// <summary>Жёсткий лимит Telegram на одно сообщение.</summary>
    public const int MaxTelegramMessageLength = 4096;

    public static string BuildBrief(AutonomousAgentDefinition definition, AutonomousRunOutput output)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(output);

        var builder = new StringBuilder();
        builder.AppendLine($"🤖 {definition.Name}");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(output.Summary)
            ? (output.Findings.Count > 0 ? "Итоги запуска:" : "Запуск завершился без сводки.")
            : output.Summary.Trim());

        if (output.Findings.Count > 0)
        {
            builder.AppendLine();
            foreach (var finding in output.Findings)
            {
                builder.AppendLine($"• {finding}");
            }
        }

        if (output.Questions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("❓ Вопросы:");
            for (var index = 0; index < output.Questions.Count; index++)
            {
                builder.AppendLine($"{index + 1}. {output.Questions[index]}");
            }
        }

        builder.AppendLine();
        builder.Append(output.Questions.Count > 0
            ? "Ответь реплаем на это сообщение — учту в следующем запуске."
            : "Можешь ответить реплаем на это сообщение, если хочешь скорректировать курс.");

        return builder.ToString();
    }

    /// <summary>
    /// Режет текст на части под лимит Telegram, предпочитая границы абзацев, затем строк, и только
    /// в крайнем случае — жёсткий разрез. Пустой/короткий текст возвращается одной частью.
    /// </summary>
    public static IReadOnlyList<string> Chunk(string text, int maxLength = MaxTelegramMessageLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 16);

        var normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length == 0)
        {
            return new[] { "(пустая сводка)" };
        }

        if (normalized.Length <= maxLength)
        {
            return new[] { normalized };
        }

        var chunks = new List<string>();
        var remaining = normalized.AsSpan();

        while (remaining.Length > maxLength)
        {
            var window = remaining[..maxLength];
            var splitAt = window.LastIndexOf("\n\n".AsSpan());
            if (splitAt <= 0)
            {
                splitAt = window.LastIndexOf('\n');
            }

            if (splitAt <= 0)
            {
                splitAt = window.LastIndexOf(' ');
            }

            if (splitAt <= 0)
            {
                // Ни одной удобной границы — режем жёстко, лишь бы не потерять текст.
                splitAt = maxLength;
            }

            chunks.Add(remaining[..splitAt].ToString().Trim());
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            chunks.Add(remaining.ToString().Trim());
        }

        return chunks;
    }
}
