using System.Text.Json;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: разбор ответа модели в структурированный AutonomousRunOutput.
/// Зачем: модель регулярно оборачивает JSON в markdown-заборчик или добавляет пояснение до/после — жёсткий Deserialize такое ломает и терял бы всю работу запуска.
/// Как: вырезает первый сбалансированный JSON-объект (учитывая строки и экранирование), разбирает его с мягкой схемой, а при неудаче отдаёт весь текст как сводку.
/// </summary>
public static class AutonomousRunOutputParser
{
    private const int MaxQuestions = 3;
    private const int MaxSummaryLength = 4_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AutonomousRunOutput Parse(string? responseText, int maxDurableFacts)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return AutonomousRunOutput.Empty;
        }

        var json = ExtractJsonObject(responseText);
        if (json is not null)
        {
            var parsed = TryParseJson(json, maxDurableFacts);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        // Модель ответила прозой — не теряем работу запуска, отдаём текст как сводку.
        return new AutonomousRunOutput(
            Truncate(responseText.Trim(), MaxSummaryLength),
            Array.Empty<string>(),
            Array.Empty<string>(),
            NextFocus: null);
    }

    private static AutonomousRunOutput? TryParseJson(string json, int maxDurableFacts)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var summary = ReadString(root, "summary");
            if (string.IsNullOrWhiteSpace(summary))
            {
                return null;
            }

            return new AutonomousRunOutput(
                Truncate(summary.Trim(), MaxSummaryLength),
                ReadStringArray(root, "questions", MaxQuestions),
                ReadStringArray(root, "durableFacts", Math.Max(0, maxDurableFacts)),
                ReadString(root, "nextFocus")?.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Ищет первый сбалансированный JSON-объект, корректно пропуская скобки внутри строк.</summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (current)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(index + 1)];
                    }

                    break;
                default:
                    break;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName, int limit)
    {
        if (limit <= 0
            || !root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            items.Add(text.Trim());
            if (items.Count >= limit)
            {
                break;
            }
        }

        return items;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    internal static JsonSerializerOptions SerializerOptions => JsonOptions;
}
