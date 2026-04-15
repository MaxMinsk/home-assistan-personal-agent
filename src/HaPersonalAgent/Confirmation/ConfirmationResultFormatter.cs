using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: formatter результата подтвержденного действия для ответа пользователю и audit details.
/// Зачем: `/approve` должен возвращать полезный итог без протаскивания raw JSON в Telegram-specific код, обычную память диалога или логи.
/// Как: строит короткий sanitized preview, редактирует secret-like поля и обрезает большие payloads.
/// </summary>
public sealed partial class ConfirmationResultFormatter
{
    private const int UserPreviewMaxLength = 1400;
    private const int AuditPreviewMaxLength = 512;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] SensitivePropertyMarkers =
    [
        "authorization",
        "password",
        "secret",
        "token",
        "api_key",
        "apikey",
        "access_token",
        "refresh_token",
    ];

    public string CreateCompletedMessage(
        PendingConfirmation confirmation,
        string? resultJson)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        var preview = CreateUserPreview(resultJson);
        if (string.IsNullOrWhiteSpace(preview))
        {
            return $"Выполнено действие {confirmation.Id}: {confirmation.Summary}";
        }

        return string.Join(
            Environment.NewLine,
            $"Выполнено действие {confirmation.Id}: {confirmation.Summary}",
            string.Empty,
            "Результат:",
            preview);
    }

    public string? CreateAuditDetails(string? resultJson)
    {
        var preview = CreateSanitizedPreview(resultJson, AuditPreviewMaxLength, wrapJson: false);

        return string.IsNullOrWhiteSpace(preview)
            ? null
            : $"Result preview: {preview}";
    }

    private static string? CreateUserPreview(string? resultJson) =>
        CreateSanitizedPreview(resultJson, UserPreviewMaxLength, wrapJson: true);

    private static string? CreateSanitizedPreview(
        string? resultJson,
        int maxLength,
        bool wrapJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        var trimmed = resultJson.Trim();
        if (TryCreateSanitizedJson(trimmed, out var sanitizedJson))
        {
            var truncatedJson = Truncate(sanitizedJson, maxLength);

            return wrapJson
                ? $"```json{Environment.NewLine}{truncatedJson}{Environment.NewLine}```"
                : truncatedJson;
        }

        return Truncate(RedactSensitiveText(trimmed), maxLength);
    }

    private static bool TryCreateSanitizedJson(
        string json,
        out string sanitizedJson)
    {
        sanitizedJson = json;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        if (root is JsonValue rootValue
            && rootValue.TryGetValue<string>(out var rootText))
        {
            sanitizedJson = JsonSerializer.Serialize(RedactSensitiveText(rootText), JsonOptions);
            return true;
        }

        RedactSensitiveJson(root);
        sanitizedJson = root.ToJsonString(JsonOptions);

        return true;
    }

    private static void RedactSensitiveJson(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (IsSensitivePropertyName(property.Key))
                {
                    jsonObject[property.Key] = "[redacted]";
                    continue;
                }

                if (property.Value is JsonValue jsonValue
                    && jsonValue.TryGetValue<string>(out var textValue))
                {
                    jsonObject[property.Key] = RedactSensitiveText(textValue);
                }
                else if (property.Value is not null)
                {
                    RedactSensitiveJson(property.Value);
                }
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                var item = jsonArray[index];
                if (item is not null)
                {
                    if (item is JsonValue jsonValue
                        && jsonValue.TryGetValue<string>(out var textValue))
                    {
                        jsonArray[index] = RedactSensitiveText(textValue);
                    }
                    else
                    {
                        RedactSensitiveJson(item);
                    }
                }
            }
        }
    }

    private static bool IsSensitivePropertyName(string propertyName)
    {
        var normalized = propertyName
            .Replace("-", "_", StringComparison.Ordinal)
            .ToLowerInvariant();

        return SensitivePropertyMarkers.Any(normalized.Contains);
    }

    private static string RedactSensitiveText(string value) =>
        SensitiveTextRegex().Replace(value, "$1=[redacted]");

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..Math.Max(0, maxLength - 16)] + "... [truncated]";

    [GeneratedRegex(
        "(authorization|password|secret|token|api[_-]?key|access[_-]?token|refresh[_-]?token)\\s*[:=]\\s*[^,;\\r\\n]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveTextRegex();
}
