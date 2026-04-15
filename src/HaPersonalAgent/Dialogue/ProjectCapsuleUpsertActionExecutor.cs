using System.Globalization;
using System.Text;
using System.Text.Json;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: executor подтвержденного upsert для project capsules.
/// Зачем: агенту нужен write-инструмент для памяти, но фактическая запись должна идти только через общий confirmation policy после `/approve`.
/// Как: парсит payload pending confirmation, нормализует поля капсулы, вычисляет версию/изменения и делает upsert в `project_capsules`.
/// Ссылки:
/// - https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentThreadAndHITL/Program.cs
/// - https://github.com/microsoft/agent-framework/blob/main/docs/decisions/0006-userapproval.md
/// </summary>
public sealed class ProjectCapsuleUpsertActionExecutor : IConfirmationActionExecutor
{
    public const string ProjectCapsuleUpsertActionKind = "project_capsule_upsert";

    private const int MaxTitleLength = 120;
    private const int MaxContentLength = 2_000;
    private const int MaxScopeLength = 80;

    private readonly ILogger<ProjectCapsuleUpsertActionExecutor> _logger;
    private readonly AgentStateRepository _stateRepository;

    public ProjectCapsuleUpsertActionExecutor(
        AgentStateRepository stateRepository,
        ILogger<ProjectCapsuleUpsertActionExecutor> logger)
    {
        _stateRepository = stateRepository;
        _logger = logger;
    }

    public string ActionKind => ProjectCapsuleUpsertActionKind;

    public async Task<ConfirmationActionExecutionResult> ExecuteAsync(
        PendingConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        if (!string.Equals(confirmation.ActionKind, ProjectCapsuleUpsertActionKind, StringComparison.Ordinal))
        {
            return ConfirmationActionExecutionResult.Failure($"Unsupported action kind '{confirmation.ActionKind}'.");
        }

        if (!TryParsePayload(confirmation.PayloadJson, out var payload, out var payloadError))
        {
            return ConfirmationActionExecutionResult.Failure(payloadError ?? "Invalid project capsule upsert payload.");
        }

        var existing = await _stateRepository.GetProjectCapsuleByKeyAsync(
            confirmation.ConversationKey,
            payload.CapsuleKey,
            cancellationToken);
        var latestRawEventId = await _stateRepository.GetLatestRawEventIdAsync(
            confirmation.ConversationKey,
            cancellationToken);
        var sourceEventId = Math.Max(
            latestRawEventId ?? existing?.SourceEventId ?? 1,
            1);
        var hasMeaningfulChange = existing is null
            || !string.Equals(existing.Title, payload.Title, StringComparison.Ordinal)
            || !string.Equals(existing.ContentMarkdown, payload.ContentMarkdown, StringComparison.Ordinal)
            || !string.Equals(existing.Scope, payload.Scope, StringComparison.Ordinal)
            || Math.Abs(existing.Confidence - payload.Confidence) >= 0.005d;
        var version = existing is null
            ? 1
            : hasMeaningfulChange
                ? existing.Version + 1
                : existing.Version;
        var updatedAtUtc = existing is null || hasMeaningfulChange
            ? DateTimeOffset.UtcNow
            : existing.UpdatedAtUtc;
        var persisted = new ProjectCapsuleMemory(
            confirmation.ConversationKey,
            payload.CapsuleKey,
            payload.Title,
            payload.ContentMarkdown,
            payload.Scope,
            payload.Confidence,
            hasMeaningfulChange || existing is null
                ? sourceEventId
                : existing.SourceEventId,
            updatedAtUtc,
            version);
        await _stateRepository.UpsertProjectCapsulesAsync(
            new[] { persisted },
            cancellationToken);

        var resultJson = JsonSerializer.Serialize(new
        {
            action = ProjectCapsuleUpsertActionKind,
            changed = hasMeaningfulChange || existing is null,
            capsuleKey = persisted.CapsuleKey,
            title = persisted.Title,
            scope = persisted.Scope,
            confidence = persisted.Confidence,
            sourceEventId = persisted.SourceEventId,
            version = persisted.Version,
            updatedAtUtc = persisted.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        });
        _logger.LogInformation(
            "Project capsule confirmation {ConfirmationId} completed for {ConversationKey}; key {CapsuleKey}, changed {Changed}, version {Version}.",
            confirmation.Id,
            confirmation.ConversationKey,
            persisted.CapsuleKey,
            hasMeaningfulChange || existing is null,
            persisted.Version);

        return ConfirmationActionExecutionResult.Success(resultJson);
    }

    private static bool TryParsePayload(
        string payloadJson,
        out ParsedPayload payload,
        out string? error)
    {
        payload = default;
        error = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            error = "Project capsule payload is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Project capsule payload must be a JSON object.";
                return false;
            }

            var root = document.RootElement;
            var capsuleKey = NormalizeCapsuleKey(ReadString(root, "capsuleKey"));
            if (string.IsNullOrWhiteSpace(capsuleKey))
            {
                error = "Project capsule payload must include non-empty capsuleKey.";
                return false;
            }

            var title = NormalizeSingleLine(ReadString(root, "title"), MaxTitleLength);
            if (string.IsNullOrWhiteSpace(title))
            {
                error = "Project capsule payload must include non-empty title.";
                return false;
            }

            var contentMarkdown = NormalizeMarkdown(ReadString(root, "contentMarkdown"), MaxContentLength);
            if (string.IsNullOrWhiteSpace(contentMarkdown))
            {
                error = "Project capsule payload must include non-empty contentMarkdown.";
                return false;
            }

            var scope = NormalizeSingleLine(
                ReadOptionalString(root, "scope") ?? "conversation",
                MaxScopeLength);
            if (string.IsNullOrWhiteSpace(scope))
            {
                scope = "conversation";
            }

            var confidence = Math.Clamp(ReadOptionalDouble(root, "confidence") ?? 0.80d, 0d, 1d);

            payload = new ParsedPayload(
                capsuleKey,
                title,
                contentMarkdown,
                scope,
                confidence);
            return true;
        }
        catch (JsonException)
        {
            error = "Project capsule payload is not valid JSON.";
            return false;
        }
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            _ => property.GetRawText(),
        };
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static double? ReadOptionalDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numericValue))
        {
            return numericValue;
        }

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(
                property.GetString(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string NormalizeCapsuleKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(capacity: key.Length);
        var previousIsUnderscore = false;
        foreach (var character in key.Trim().ToLowerInvariant())
        {
            var nextCharacter = char.IsLetterOrDigit(character)
                ? character
                : '_';
            if (nextCharacter == '_')
            {
                if (previousIsUnderscore)
                {
                    continue;
                }

                previousIsUnderscore = true;
                normalized.Append(nextCharacter);
                continue;
            }

            previousIsUnderscore = false;
            normalized.Append(nextCharacter);
        }

        return normalized
            .ToString()
            .Trim('_');
    }

    private static string NormalizeSingleLine(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }

    private static string NormalizeMarkdown(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        while (normalized.Contains("\n\n\n", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }

    /// <summary>
    /// Что: нормализованный payload для upsert одной project capsule.
    /// Зачем: executor отделяет валидацию JSON от выполнения записи и работает с безопасной внутренней структурой.
    /// Как: собирается из `pending_confirmations.payload_json` после нормализации ключа/текста/чисел.
    /// </summary>
    private readonly record struct ParsedPayload(
        string CapsuleKey,
        string Title,
        string ContentMarkdown,
        string Scope,
        double Confidence);
}
