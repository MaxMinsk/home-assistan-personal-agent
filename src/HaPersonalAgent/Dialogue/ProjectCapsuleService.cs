using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: сервис извлечения и выдачи project capsules поверх raw event store.
/// Зачем: после HAAG-040/HAAG-043 нужен следующий memory-слой: устойчивые карточки проектов, которые не теряются при trim диалога.
/// Как: по manual/auto-batched триггеру читает новые raw_events, вызывает LLM в Summarization профиле, сохраняет капсулы в SQLite и готовит prompt context.
/// Ссылки:
/// - https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentWithMemory/AgentWithMemory_Step01_ChatHistoryMemory/Program.cs
/// - https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentWithMemory/AgentWithMemory_Step02_MemoryUsingMem0/Program.cs
/// </summary>
public sealed class ProjectCapsuleService
{
    private const int MaxCapsulesInPrompt = 4;
    private const int MaxRawEventsPerExtraction = 80;
    private const int MaxRawEventPayloadLength = 240;
    private const int MaxCapsuleMarkdownLength = 900;
    private const int MaxPromptCapsuleContextLength = 2_400;
    private const int DefaultAutoBatchThreshold = 20;

    private readonly IAgentRuntime _agentRuntime;
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly ILogger<ProjectCapsuleService> _logger;
    private readonly AgentStateRepository _stateRepository;

    public ProjectCapsuleService(
        IAgentRuntime agentRuntime,
        IOptions<AgentOptions> agentOptions,
        AgentStateRepository stateRepository,
        ILogger<ProjectCapsuleService> logger)
    {
        _agentRuntime = agentRuntime;
        _agentOptions = agentOptions;
        _stateRepository = stateRepository;
        _logger = logger;
    }

    public async Task<ProjectCapsulePromptContext> BuildPromptContextAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        var capsules = await _stateRepository.GetProjectCapsulesAsync(
            conversationKey,
            MaxCapsulesInPrompt,
            cancellationToken);
        if (capsules.Count == 0)
        {
            return new ProjectCapsulePromptContext(null, 0);
        }

        var builder = new StringBuilder(
            """
            Persisted project capsules (derived memory from earlier raw events).
            Use them as long-term context, but prioritize explicit user corrections and newest turns.
            """);
        foreach (var capsule in capsules)
        {
            builder.AppendLine();
            builder.Append("- [");
            builder.Append(capsule.CapsuleKey);
            builder.Append("] ");
            builder.Append(capsule.Title);
            builder.Append(" (confidence ");
            builder.Append(capsule.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
            builder.Append(", source #");
            builder.Append(capsule.SourceEventId.ToString(CultureInfo.InvariantCulture));
            builder.Append(", v");
            builder.Append(capsule.Version.ToString(CultureInfo.InvariantCulture));
            builder.Append(')');
            builder.AppendLine();
            builder.Append("  ");
            builder.AppendLine(NormalizeSingleLine(capsule.ContentMarkdown, 280));
        }

        var text = builder.ToString();
        if (text.Length > MaxPromptCapsuleContextLength)
        {
            text = text[..MaxPromptCapsuleContextLength];
        }

        return new ProjectCapsulePromptContext(text, capsules.Count);
    }

    public async Task<bool> ShouldAutoRefreshAsync(
        string conversationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationKey);

        if (!IsAutoBatchedMode(_agentOptions.Value.CapsuleExtractionMode))
        {
            return false;
        }

        var state = await _stateRepository.GetProjectCapsuleExtractionStateAsync(
            conversationKey,
            cancellationToken);
        var rawEventsSinceLastExtraction = await _stateRepository.GetRawEventCountSinceIdAsync(
            conversationKey,
            state?.LastRawEventId ?? 0,
            cancellationToken);
        var threshold = Math.Clamp(
            _agentOptions.Value.CapsuleAutoBatchRawEventThreshold,
            4,
            500);

        return rawEventsSinceLastExtraction >= threshold;
    }

    public async Task<ProjectCapsuleRefreshResult> RefreshAsync(
        DialogueConversation conversation,
        string correlationId,
        bool force,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var conversationKey = DialogueConversationKey.Create(conversation);
        var latestRawEventId = await _stateRepository.GetLatestRawEventIdAsync(
            conversationKey,
            cancellationToken);
        if (!latestRawEventId.HasValue)
        {
            return new ProjectCapsuleRefreshResult(
                IsConfigured: true,
                IsUpdated: false,
                "Для этого чата пока нет raw events для извлечения капсул.");
        }

        var state = await _stateRepository.GetProjectCapsuleExtractionStateAsync(
            conversationKey,
            cancellationToken);
        var startRawEventId = force
            ? 0
            : state?.LastRawEventId ?? 0;
        if (!force && latestRawEventId.Value <= startRawEventId)
        {
            var existingCount = await _stateRepository.GetProjectCapsuleCountAsync(
                conversationKey,
                cancellationToken);
            return new ProjectCapsuleRefreshResult(
                IsConfigured: true,
                IsUpdated: false,
                "Новых raw events для обновления капсул пока нет.",
                existingCount,
                startRawEventId);
        }

        var rawEvents = await _stateRepository.GetRawEventsSinceIdAsync(
            conversationKey,
            startRawEventId,
            MaxRawEventsPerExtraction,
            cancellationToken);
        var extractionEvents = rawEvents
            .Where(rawEvent => !string.Equals(rawEvent.EventKind, DialogueRawEventKinds.ContextReset, StringComparison.Ordinal))
            .ToArray();
        if (extractionEvents.Length == 0)
        {
            return new ProjectCapsuleRefreshResult(
                IsConfigured: true,
                IsUpdated: false,
                "Нечего извлекать: в новом диапазоне есть только служебные события.",
                await _stateRepository.GetProjectCapsuleCountAsync(conversationKey, cancellationToken),
                startRawEventId);
        }

        var existingCapsules = await _stateRepository.GetProjectCapsulesAsync(
            conversationKey,
            limit: 50,
            cancellationToken);
        var request = BuildExtractionRequest(existingCapsules, extractionEvents);
        var runtimeResponse = await _agentRuntime.SendAsync(
            request,
            AgentContext.Create(
                correlationId: correlationId,
                shouldRefreshPersistedSummary: false,
                forcePersistedSummaryRefresh: false,
                messagesSincePersistedSummary: 0,
                conversationKey: conversationKey,
                transport: conversation.Transport,
                conversationId: conversation.ConversationId,
                participantId: conversation.ParticipantId,
                executionProfile: LlmExecutionProfile.Summarization),
            cancellationToken);

        if (!runtimeResponse.IsConfigured)
        {
            return new ProjectCapsuleRefreshResult(
                IsConfigured: false,
                IsUpdated: false,
                runtimeResponse.Text,
                existingCapsules.Count,
                startRawEventId);
        }

        var parsedCapsules = ParseCapsules(runtimeResponse.Text);
        if (parsedCapsules.Count == 0)
        {
            _logger.LogWarning(
                "Project capsules refresh {CorrelationId} returned no parseable capsules for {ConversationKey}.",
                correlationId,
                conversationKey);
            return new ProjectCapsuleRefreshResult(
                IsConfigured: true,
                IsUpdated: false,
                "Не удалось извлечь капсулы из ответа модели. Попробуй позже.",
                existingCapsules.Count,
                startRawEventId);
        }

        var mergedCapsules = MergeCapsules(
            conversationKey,
            existingCapsules,
            parsedCapsules,
            latestRawEventId.Value,
            DateTimeOffset.UtcNow);
        await _stateRepository.UpsertProjectCapsulesAsync(
            mergedCapsules,
            cancellationToken);
        await _stateRepository.UpsertProjectCapsuleExtractionStateAsync(
            new ProjectCapsuleExtractionState(
                conversationKey,
                latestRawEventId.Value,
                DateTimeOffset.UtcNow,
                (state?.RunsCount ?? 0) + 1),
            cancellationToken);

        var totalCapsuleCount = await _stateRepository.GetProjectCapsuleCountAsync(
            conversationKey,
            cancellationToken);
        var updatedCapsules = mergedCapsules.Count(capsule =>
        {
            var existing = existingCapsules.FirstOrDefault(item =>
                string.Equals(item.CapsuleKey, capsule.CapsuleKey, StringComparison.Ordinal));
            return existing is null || existing.Version != capsule.Version;
        });

        _logger.LogInformation(
            "Project capsules refresh {CorrelationId} completed for {ConversationKey}; parsed {ParsedCapsules}, upserted {UpsertedCapsules}, updated {UpdatedCapsules}, total {TotalCapsules}, last raw event id {LastRawEventId}.",
            correlationId,
            conversationKey,
            parsedCapsules.Count,
            mergedCapsules.Count,
            updatedCapsules,
            totalCapsuleCount,
            latestRawEventId.Value);

        return new ProjectCapsuleRefreshResult(
            IsConfigured: true,
            IsUpdated: updatedCapsules > 0,
            Message: updatedCapsules > 0
                ? $"Капсулы обновлены: {updatedCapsules} changed, всего {totalCapsuleCount}."
                : $"Извлечение выполнено без изменений, всего капсул: {totalCapsuleCount}.",
            totalCapsuleCount,
            latestRawEventId.Value);
    }

    private static bool IsAutoBatchedMode(string mode) =>
        string.Equals(mode?.Trim(), AgentOptions.CapsuleExtractionModeAutoBatched, StringComparison.OrdinalIgnoreCase);

    private static string BuildExtractionRequest(
        IReadOnlyList<ProjectCapsuleMemory> existingCapsules,
        IReadOnlyList<RawEventRecord> rawEvents)
    {
        var prompt = new StringBuilder(
            """
            Service request: refresh project capsules from raw events.
            Return JSON only, no markdown, no prose.

            Expected JSON schema:
            {
              "capsules": [
                {
                  "key": "ascii_snake_case_key",
                  "title": "short title in Russian",
                  "contentMarkdown": "structured markdown summary (facts, status, next actions, constraints, risks)",
                  "scope": "conversation",
                  "confidence": 0.0,
                  "sourceEventId": 123
                }
              ]
            }

            Rules:
            - Keep or update only stable, practical long-term project knowledge.
            - Do not include secrets, tokens, internal errors, approval ids, or transient chat noise.
            - Prefer concrete facts and decisions over emotional filler.
            - key must be stable and deterministic.
            - confidence must be in [0.0, 1.0].
            - sourceEventId must reference one of input raw events.
            - If no useful project memory is present, return {"capsules": []}.
            """);

        if (existingCapsules.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Current persisted capsules baseline (JSON):");
            prompt.AppendLine("[");
            foreach (var capsule in existingCapsules)
            {
                prompt.AppendLine(
                    JsonSerializer.Serialize(
                        new
                        {
                            key = capsule.CapsuleKey,
                            title = capsule.Title,
                            contentMarkdown = capsule.ContentMarkdown,
                            scope = capsule.Scope,
                            confidence = capsule.Confidence,
                            sourceEventId = capsule.SourceEventId,
                        }));
            }

            prompt.AppendLine("]");
        }

        prompt.AppendLine();
        prompt.AppendLine("New raw events to process:");
        foreach (var rawEvent in rawEvents)
        {
            prompt.Append("- #");
            prompt.Append(rawEvent.Id.ToString(CultureInfo.InvariantCulture));
            prompt.Append(" | ");
            prompt.Append(rawEvent.EventKind);
            prompt.Append(" | ");
            prompt.AppendLine(NormalizeSingleLine(rawEvent.Payload, MaxRawEventPayloadLength));
        }

        return prompt.ToString();
    }

    private static IReadOnlyList<ParsedProjectCapsule> ParseCapsules(string text)
    {
        var json = TryExtractJson(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ParsedProjectCapsule>();
        }

        try
        {
            var payload = JsonSerializer.Deserialize<CapsuleExtractionPayload>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            if (payload?.Capsules is null || payload.Capsules.Count == 0)
            {
                return Array.Empty<ParsedProjectCapsule>();
            }

            return payload.Capsules
                .Where(capsule => !string.IsNullOrWhiteSpace(capsule.Key))
                .Select(capsule => new ParsedProjectCapsule(
                    NormalizeCapsuleKey(capsule.Key),
                    NormalizeTitle(capsule.Title),
                    NormalizeContent(capsule.ContentMarkdown),
                    string.IsNullOrWhiteSpace(capsule.Scope) ? "conversation" : capsule.Scope.Trim(),
                    Math.Clamp(capsule.Confidence, 0d, 1d),
                    capsule.SourceEventId))
                .Where(capsule =>
                    !string.IsNullOrWhiteSpace(capsule.Key)
                    && !string.IsNullOrWhiteSpace(capsule.Title)
                    && !string.IsNullOrWhiteSpace(capsule.ContentMarkdown))
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<ParsedProjectCapsule>();
        }
    }

    private static string? TryExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            return null;
        }

        return trimmed[firstBrace..(lastBrace + 1)];
    }

    private static IReadOnlyList<ProjectCapsuleMemory> MergeCapsules(
        string conversationKey,
        IReadOnlyList<ProjectCapsuleMemory> existingCapsules,
        IReadOnlyList<ParsedProjectCapsule> parsedCapsules,
        long latestRawEventId,
        DateTimeOffset now)
    {
        var existingByKey = existingCapsules
            .GroupBy(capsule => capsule.CapsuleKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var merged = new List<ProjectCapsuleMemory>(parsedCapsules.Count);

        foreach (var parsed in parsedCapsules)
        {
            var sourceEventId = parsed.SourceEventId > 0
                ? Math.Min(parsed.SourceEventId, latestRawEventId)
                : latestRawEventId;

            if (!existingByKey.TryGetValue(parsed.Key, out var existing))
            {
                merged.Add(new ProjectCapsuleMemory(
                    conversationKey,
                    parsed.Key,
                    parsed.Title,
                    parsed.ContentMarkdown,
                    parsed.Scope,
                    parsed.Confidence,
                    sourceEventId,
                    now,
                    Version: 1));
                continue;
            }

            var hasMeaningfulChange =
                !string.Equals(existing.Title, parsed.Title, StringComparison.Ordinal)
                || !string.Equals(existing.ContentMarkdown, parsed.ContentMarkdown, StringComparison.Ordinal)
                || !string.Equals(existing.Scope, parsed.Scope, StringComparison.Ordinal)
                || Math.Abs(existing.Confidence - parsed.Confidence) >= 0.005d
                || existing.SourceEventId != sourceEventId;
            var nextVersion = hasMeaningfulChange
                ? existing.Version + 1
                : existing.Version;
            merged.Add(new ProjectCapsuleMemory(
                conversationKey,
                parsed.Key,
                parsed.Title,
                parsed.ContentMarkdown,
                parsed.Scope,
                parsed.Confidence,
                sourceEventId,
                hasMeaningfulChange ? now : existing.UpdatedAtUtc,
                nextVersion));
        }

        return merged;
    }

    private static string NormalizeCapsuleKey(string key)
    {
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

    private static string NormalizeTitle(string? title) =>
        (title ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\n', ' ')
        .Trim();

    private static string NormalizeContent(string? contentMarkdown)
    {
        var normalized = (contentMarkdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
        while (normalized.Contains("\n\n\n", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }

        return normalized.Length <= MaxCapsuleMarkdownLength
            ? normalized
            : normalized[..MaxCapsuleMarkdownLength];
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

        return normalized[..maxLength] + "...";
    }

    private sealed record ParsedProjectCapsule(
        string Key,
        string Title,
        string ContentMarkdown,
        string Scope,
        double Confidence,
        long SourceEventId);

    private sealed class CapsuleExtractionPayload
    {
        [JsonPropertyName("capsules")]
        public List<CapsuleExtractionItem> Capsules { get; init; } = [];
    }

    private sealed class CapsuleExtractionItem
    {
        [JsonPropertyName("key")]
        public string Key { get; init; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("contentMarkdown")]
        public string ContentMarkdown { get; init; } = string.Empty;

        [JsonPropertyName("scope")]
        public string Scope { get; init; } = "conversation";

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }

        [JsonPropertyName("sourceEventId")]
        public long SourceEventId { get; init; }
    }
}
