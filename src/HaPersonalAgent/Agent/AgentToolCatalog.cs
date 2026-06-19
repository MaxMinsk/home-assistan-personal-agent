using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.HomeAssistant;
using HaPersonalAgent.Memory;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: каталог agent tools и runtime instructions для конкретного run.
/// Зачем: сборка tools/instructions имеет отдельную ответственность и не должна раздувать orchestration-класс.
/// Как: по execution profile, context и доступным инфраструктурным зависимостям возвращает список AITool и текст инструкций.
/// </summary>
public sealed class AgentToolCatalog
{
    private const int ProjectCapsuleToolListLimit = 8;
    private const int ProjectCapsuleListPreviewLength = 320;
    private const int ConversationMemorySearchDefaultTopK = 4;
    private const int ConversationMemorySearchMaxTopK = 25;
    private static readonly JsonSerializerOptions ToolJsonOptions = new(JsonSerializerDefaults.Web);

    private const string BaseInstructions =
        """
        You are Home Assistant Personal Agent, a learning-first assistant built to explore Microsoft Agent Framework.
        Keep answers concise and practical.
        Use the status tool when the user asks about application status, version, uptime, configuration mode, or health.
        Use the home_assistant_mcp_status tool when the user asks whether MCP is available, why Home Assistant access fails, or which Home Assistant MCP tools are visible.
        Use Home Assistant MCP tools only for read-only questions about current home state, history, or diagnostics.
        For Home Assistant requests that change state, call propose_home_assistant_mcp_action when it is available.
        Use project_capsules_list and project_capsule_get to inspect durable memory capsules for this conversation.
        To create or update durable memory capsules, call propose_project_capsule_upsert and wait for explicit user approval before claiming the update is applied.
        When the memory tools are available, use memory_recall to look up durable long-term facts about the user before answering, and call propose_memory_save to persist an important durable fact (it requires explicit user approval, like other write actions).
        Never claim a Home Assistant control action was executed until the app reports completion after user approval.
        Never reveal secrets or raw tokens.
        """;

    private readonly AgentStatusTool _statusTool;
    private readonly HomeAssistantMcpStatusTool? _homeAssistantMcpStatusTool;
    private readonly AgentStateRepository? _stateRepository;
    private readonly IConfirmationService? _confirmationService;
    private readonly IMemoryMcpClient? _memoryMcpClient;
    private readonly IOptions<MemoryMcpOptions>? _memoryMcpOptions;
    private readonly ILogger<AgentToolCatalog> _logger;

    public AgentToolCatalog(
        AgentStatusTool statusTool,
        ILogger<AgentToolCatalog> logger,
        HomeAssistantMcpStatusTool? homeAssistantMcpStatusTool = null,
        AgentStateRepository? stateRepository = null,
        IConfirmationService? confirmationService = null,
        IMemoryMcpClient? memoryMcpClient = null,
        IOptions<MemoryMcpOptions>? memoryMcpOptions = null)
    {
        _statusTool = statusTool ?? throw new ArgumentNullException(nameof(statusTool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _homeAssistantMcpStatusTool = homeAssistantMcpStatusTool;
        _stateRepository = stateRepository;
        _confirmationService = confirmationService;
        _memoryMcpClient = memoryMcpClient;
        _memoryMcpOptions = memoryMcpOptions;
    }

    public IReadOnlyList<AITool> CreateTools(
        AgentContext context,
        LlmExecutionPlan executionPlan,
        HomeAssistantMcpAgentToolSet homeAssistantMcpTools)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(homeAssistantMcpTools);

        var tools = new List<AITool>();
        if (!executionPlan.UsesTools)
        {
            return tools;
        }

        var statusTool = AIFunctionFactory.Create(
            (Func<AgentStatusSnapshot>)_statusTool.GetStatus,
            name: "status",
            description: "Returns non-secret application version, uptime, configuration mode, and health details.",
            serializerOptions: null);
        tools.Add(statusTool);

        if (_homeAssistantMcpStatusTool is not null)
        {
            tools.Add(AIFunctionFactory.Create(
                (Func<CancellationToken, Task<HomeAssistantMcpDiscoveryResult>>)_homeAssistantMcpStatusTool.GetStatusAsync,
                name: "home_assistant_mcp_status",
                description: "Returns Home Assistant MCP reachability, auth state, endpoint, and discovered tools/prompts without revealing tokens.",
                serializerOptions: null));
        }

        tools.AddRange(homeAssistantMcpTools.Tools);

        if (_confirmationService is not null
            && homeAssistantMcpTools.ConfirmationRequiredTools.Count > 0)
        {
            tools.Add(CreateHomeAssistantActionProposalTool(
                context,
                homeAssistantMcpTools.ConfirmationRequiredTools));
        }

        if (_stateRepository is not null)
        {
            tools.Add(CreateProjectCapsulesListTool(context));
            tools.Add(CreateProjectCapsuleGetTool(context));

            if (_confirmationService is not null)
            {
                tools.Add(CreateProjectCapsuleUpsertProposalTool(context));
            }
        }

        if (_memoryMcpClient is not null)
        {
            tools.Add(CreateMemoryMcpStatusTool());
        }

        if (_memoryMcpClient is not null && _memoryMcpOptions?.Value.IsConfigured == true)
        {
            tools.Add(CreateMemoryRecallTool(context));
            if (_confirmationService is not null)
            {
                tools.Add(CreateMemorySaveProposalTool(context));
            }
        }

        return tools;
    }

    public string CreateInstructions(
        AgentContext context,
        LlmExecutionPlan executionPlan,
        HomeAssistantMcpAgentToolSet homeAssistantMcpTools)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(homeAssistantMcpTools);

        var instructions = new StringBuilder(BaseInstructions);

        instructions.AppendLine();
        instructions.AppendLine($"Current date/time (UTC): {DateTimeOffset.UtcNow:O}. Use this for any age, relative-date (\"tomorrow\"/\"in a week\"), scheduling, or memory-timestamp reasoning; do not rely on a date remembered from earlier in the conversation.");

        if (!executionPlan.UsesTools)
        {
            instructions.AppendLine();
            instructions.AppendLine(
                "This run uses a no-tools profile. Do not claim to inspect Home Assistant, call tools, read files, or control devices.");
            instructions.AppendLine(
                "This is the cost-optimized profile (" + context.ExecutionProfile + "); tools are intentionally withheld here, this is NOT an error or outage.");
            instructions.AppendLine(
                "If the user asks for live home state or control, explain that this requires the normal tool-enabled dialogue profile.");
            return instructions.ToString();
        }

        instructions.AppendLine();
        instructions.AppendLine("Active execution profile: " + context.ExecutionProfile + ".");

        if (_confirmationService is not null
            && homeAssistantMcpTools.ConfirmationRequiredTools.Count > 0)
        {
            instructions.AppendLine();
            instructions.AppendLine(
                "Home Assistant control workflow: prepare actions only through propose_home_assistant_mcp_action with exact toolName, JSON object argumentsJson, short summary, and explicit risk.");
            instructions.AppendLine(
                "After proposal, tell the user to approve or reject with the commands returned by the tool.");
            instructions.AppendLine("Confirmation-required MCP tools: " + FormatToolList(homeAssistantMcpTools.ConfirmationRequiredTools, maxTools: 30));
        }
        else
        {
            instructions.AppendLine();
            instructions.AppendLine("Home Assistant control workflow is unavailable in this run; explain that control requires confirmation.");
        }

        instructions.AppendLine();
        instructions.AppendLine("Conversation memory retrieval mode: " + context.MemoryRetrievalMode + ".");
        if (string.Equals(context.MemoryRetrievalMode, AgentOptions.MemoryRetrievalModeOnDemandTool, StringComparison.Ordinal))
        {
            instructions.AppendLine("Long-term memory is not auto-injected in this mode; call memory_recall when you need durable facts from long-term memory.");
        }
        else
        {
            instructions.AppendLine("Relevant durable long-term memory is auto-injected as context before this run when long-term memory is available.");
        }

        if (_memoryMcpClient is not null && _memoryMcpOptions?.Value.IsConfigured == true)
        {
            instructions.AppendLine("Long-term memory tools are available: memory_recall (read durable facts), propose_memory_save (save a durable fact via approval), memory_mcp_status (check Memory MCP availability).");
            instructions.AppendLine("Grounding rule (no fabrication): answer from memory only when a tool actually returned matching results. If memory_recall or the auto-injected context returns no hits, say plainly that you found nothing and ask the user — never invent facts, lists, variety names, or counts to fill the gap. Never claim you saved or updated a note unless a save/upsert tool reported success; proposing a save still needs explicit user approval.");
            instructions.AppendLine("memory_recall does full-text relevance search inside the user's home domain — pass a short natural-language query (e.g. 'pepper seed varieties', 'dog feeding schedule'), not tag syntax like 'crop:pepper'.");
            instructions.AppendLine("Counting and listing: a recall result shows only a page of snippets but reports the full match count as `total`. Answer 'how many X' from `total`, not from the number of snippets shown. To enumerate every item, if `hasMore` is true call memory_recall again with a larger topK or the next offset until you have covered `total` before listing.");
        }

        instructions.AppendLine();
        instructions.AppendLine("Use project_capsules_list/project_capsule_get when you need long-term project facts from capsule memory.");
        instructions.AppendLine("Memory model: project capsules (project_capsules_list/get, propose_project_capsule_upsert) are structured, conversation-scoped cards about ongoing topics/projects; memory_recall / propose_memory_save handle ad-hoc durable facts and preferences in long-term memory. Use capsules for structured project state, memory_save for standalone facts.");

        if (_stateRepository is null)
        {
            instructions.AppendLine("Project capsule storage is unavailable in this runtime; explain that capsule tools are disabled.");
        }
        else if (_confirmationService is null)
        {
            instructions.AppendLine("Project capsule write workflow is unavailable because confirmation service is not registered.");
        }
        else
        {
            instructions.AppendLine("For capsule writes, use propose_project_capsule_upsert and wait for user approval before confirming any memory update.");
        }

        return instructions.ToString();
    }

    private AIFunction CreateProjectCapsulesListTool(AgentContext context)
    {
        async Task<string> ListProjectCapsulesAsync(CancellationToken cancellationToken)
        {
            if (_stateRepository is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Project capsule storage is not registered.",
                }, ToolJsonOptions);
            }

            if (string.IsNullOrWhiteSpace(context.ConversationKey))
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Conversation scope is missing for this run.",
                }, ToolJsonOptions);
            }

            var capsules = await _stateRepository.GetProjectCapsulesAsync(
                context.ConversationKey,
                ProjectCapsuleToolListLimit,
                cancellationToken);
            var payload = new
            {
                available = true,
                count = capsules.Count,
                capsules = capsules.Select(capsule => new
                {
                    key = capsule.CapsuleKey,
                    title = capsule.Title,
                    scope = capsule.Scope,
                    confidence = Math.Round(capsule.Confidence, 3),
                    sourceEventId = capsule.SourceEventId,
                    version = capsule.Version,
                    updatedAtUtc = capsule.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    contentPreview = NormalizeSingleLine(
                        capsule.ContentMarkdown,
                        ProjectCapsuleListPreviewLength),
                }),
            };

            return JsonSerializer.Serialize(payload, ToolJsonOptions);
        }

        return AIFunctionFactory.Create(
            (Func<CancellationToken, Task<string>>)ListProjectCapsulesAsync,
            name: "project_capsules_list",
            description: $"Returns metadata and short previews for up to {ProjectCapsuleToolListLimit} latest project capsules. Use project_capsule_get for full content.",
            serializerOptions: null);
    }

    private AIFunction CreateProjectCapsuleGetTool(AgentContext context)
    {
        async Task<string> GetProjectCapsuleAsync(string capsuleKey, CancellationToken cancellationToken)
        {
            if (_stateRepository is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Project capsule storage is not registered.",
                }, ToolJsonOptions);
            }

            if (string.IsNullOrWhiteSpace(context.ConversationKey))
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Conversation scope is missing for this run.",
                }, ToolJsonOptions);
            }

            var normalizedCapsuleKey = NormalizeCapsuleKey(capsuleKey);
            if (string.IsNullOrWhiteSpace(normalizedCapsuleKey))
            {
                return JsonSerializer.Serialize(new
                {
                    available = true,
                    found = false,
                    reason = "capsuleKey must be non-empty.",
                }, ToolJsonOptions);
            }

            var capsule = await _stateRepository.GetProjectCapsuleByKeyAsync(
                context.ConversationKey,
                normalizedCapsuleKey,
                cancellationToken);
            if (capsule is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = true,
                    found = false,
                    key = normalizedCapsuleKey,
                }, ToolJsonOptions);
            }

            return JsonSerializer.Serialize(new
            {
                available = true,
                found = true,
                capsule = new
                {
                    key = capsule.CapsuleKey,
                    title = capsule.Title,
                    scope = capsule.Scope,
                    confidence = Math.Round(capsule.Confidence, 3),
                    sourceEventId = capsule.SourceEventId,
                    version = capsule.Version,
                    updatedAtUtc = capsule.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    contentMarkdown = capsule.ContentMarkdown,
                },
            }, ToolJsonOptions);
        }

        return AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<string>>)GetProjectCapsuleAsync,
            name: "project_capsule_get",
            description: "Returns one project capsule by capsuleKey (ASCII snake_case preferred) as JSON for this conversation.",
            serializerOptions: null);
    }

    private AIFunction CreateProjectCapsuleUpsertProposalTool(AgentContext context)
    {
        Task<ConfirmationProposalResult> ProposeProjectCapsuleUpsertAsync(
            string capsuleKey,
            string title,
            string contentMarkdown,
            string scope,
            double confidence,
            string summary,
            string risk,
            CancellationToken cancellationToken)
        {
            if (_confirmationService is null)
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("Confirmation service сейчас недоступен."));
            }

            var normalizedCapsuleKey = NormalizeCapsuleKey(capsuleKey);
            if (string.IsNullOrWhiteSpace(normalizedCapsuleKey))
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("capsuleKey должен быть непустым и в формате ASCII snake_case."));
            }

            var normalizedTitle = NormalizeSingleLine(title, maxLength: 120);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("title не может быть пустым."));
            }

            var normalizedContentMarkdown = NormalizeMarkdown(contentMarkdown, maxLength: 2_000);
            if (string.IsNullOrWhiteSpace(normalizedContentMarkdown))
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("contentMarkdown не может быть пустым."));
            }

            var normalizedScope = NormalizeSingleLine(scope, maxLength: 80);
            if (string.IsNullOrWhiteSpace(normalizedScope))
            {
                normalizedScope = "conversation";
            }

            var payloadJson = JsonSerializer.Serialize(new
            {
                capsuleKey = normalizedCapsuleKey,
                title = normalizedTitle,
                contentMarkdown = normalizedContentMarkdown,
                scope = normalizedScope,
                confidence = Math.Clamp(confidence, 0d, 1d),
            }, ToolJsonOptions);

            return _confirmationService.ProposeAsync(
                new ConfirmationProposalRequest(
                    context,
                    ProjectCapsuleUpsertActionExecutor.ProjectCapsuleUpsertActionKind,
                    OperationName: $"upsert_project_capsule:{normalizedCapsuleKey}",
                    payloadJson,
                    summary,
                    risk),
                cancellationToken);
        }

        return AIFunctionFactory.Create(
            (Func<string, string, string, string, double, string, string, CancellationToken, Task<ConfirmationProposalResult>>)ProposeProjectCapsuleUpsertAsync,
            name: "propose_project_capsule_upsert",
            description: "Creates pending confirmation to create/update one project capsule. Arguments: capsuleKey, title, contentMarkdown, scope, confidence(0..1), summary, risk.",
            serializerOptions: null);
    }

    private AIFunction CreateHomeAssistantActionProposalTool(
        AgentContext context,
        IReadOnlyCollection<HomeAssistantMcpItemInfo> confirmationRequiredTools)
    {
        Task<ConfirmationProposalResult> ProposeHomeAssistantMcpActionAsync(
            string toolName,
            string argumentsJson,
            string summary,
            string risk,
            CancellationToken cancellationToken)
        {
            var matchedTool = confirmationRequiredTools.FirstOrDefault(tool =>
                string.Equals(tool.Name, toolName, StringComparison.Ordinal));
            if (matchedTool is null)
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected(
                    $"Не могу создать действие: MCP tool '{toolName}' недоступен как confirmation-required tool в текущей Home Assistant session."));
            }

            return _confirmationService!.ProposeAsync(
                new ConfirmationProposalRequest(
                    context,
                    HomeAssistantMcpActionExecutor.HomeAssistantMcpActionKind,
                    matchedTool.Name,
                    argumentsJson,
                    summary,
                    risk),
                cancellationToken);
        }

        return AIFunctionFactory.Create(
            (Func<string, string, string, string, CancellationToken, Task<ConfirmationProposalResult>>)ProposeHomeAssistantMcpActionAsync,
            name: "propose_home_assistant_mcp_action",
            description: "Creates a pending Home Assistant MCP control action. Use exact toolName and JSON object string argumentsJson. Available confirmation-required tools: "
                + FormatToolList(confirmationRequiredTools, maxTools: 20),
            serializerOptions: null);
    }

    private static string FormatToolList(
        IReadOnlyCollection<HomeAssistantMcpItemInfo> tools,
        int maxTools)
    {
        if (tools.Count == 0)
        {
            return "none";
        }

        var formattedTools = tools
            .Take(maxTools)
            .Select(tool => string.IsNullOrWhiteSpace(tool.Description)
                ? tool.Name
                : $"{tool.Name} ({Truncate(tool.Description, 100)})");
        var suffix = tools.Count > maxTools
            ? $" and {tools.Count - maxTools} more"
            : string.Empty;

        return string.Join("; ", formattedTools) + suffix;
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

    private AIFunction CreateMemoryMcpStatusTool()
    {
        async Task<string> GetMemoryMcpStatusAsync(CancellationToken cancellationToken)
        {
            var result = await _memoryMcpClient!.DiscoverAsync(cancellationToken);
            return JsonSerializer.Serialize(new
            {
                configured = _memoryMcpOptions?.Value.IsConfigured ?? false,
                status = result.Status.ToString(),
                serverVersion = result.ServerVersion,
                toolCount = result.ToolCount,
                endpoint = result.EndpointUrl,
                storeType = _memoryMcpOptions?.Value.StoreType,
            }, ToolJsonOptions);
        }

        return AIFunctionFactory.Create(
            (Func<CancellationToken, Task<string>>)GetMemoryMcpStatusAsync,
            name: "memory_mcp_status",
            description: "Returns Memory MCP reachability, server version, tool count, and the active memory store type, without exposing the token.",
            serializerOptions: null);
    }

    private AIFunction CreateMemoryRecallTool(AgentContext context)
    {
        async Task<string> RecallMemoryAsync(
            string query,
            int topK,
            int offset,
            CancellationToken cancellationToken)
        {
            if (_memoryMcpClient is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Memory MCP is not configured.",
                }, ToolJsonOptions);
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return JsonSerializer.Serialize(new
                {
                    available = true,
                    count = 0,
                    reason = "query must be non-empty.",
                }, ToolJsonOptions);
            }

            var normalizedTopK = Math.Clamp(topK <= 0 ? ConversationMemorySearchDefaultTopK : topK, 1, ConversationMemorySearchMaxTopK);
            var normalizedOffset = Math.Max(0, offset);
            try
            {
                var result = await _memoryMcpClient.CallToolAsync(
                    "notes_search",
                    new Dictionary<string, object?>
                    {
                        // Scope by domain only — see BoundedChatHistoryProvider.RecallFromMemoryMcpAsync:
                        // a marker-tag filter ANDs against every note and hides the user's imported
                        // notes (seed lists etc.), so recall returned nothing for real questions.
                        ["domain"] = MemoryMcpSaveActionExecutor.MemoryDomain,
                        // notes_search ANDs query tokens — reduce a natural-language query to content
                        // tokens with prefix matching so questions don't AND-match to nothing.
                        ["query"] = MemoryRecallQueryBuilder.Build(query),
                        ["limit"] = normalizedTopK,
                        ["offset"] = normalizedOffset,
                    },
                    cancellationToken);

                // Surface the notes_search envelope's total/hasMore so the model answers "how many"
                // from `total` (not the count of visible snippets) and knows when to paginate.
                int? total = null;
                bool? hasMore = null;
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    try
                    {
                        using var envelope = JsonDocument.Parse(result.Text);
                        var root = envelope.RootElement;
                        if (root.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt32(out var totalValue))
                        {
                            total = totalValue;
                        }

                        if (root.TryGetProperty("hasMore", out var hasMoreElement)
                            && (hasMoreElement.ValueKind == JsonValueKind.True || hasMoreElement.ValueKind == JsonValueKind.False))
                        {
                            hasMore = hasMoreElement.GetBoolean();
                        }
                    }
                    catch (JsonException)
                    {
                        // Best-effort: if the envelope is not parseable, omit total/hasMore.
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    available = true,
                    query = NormalizeSingleLine(query, maxLength: 240),
                    topK = normalizedTopK,
                    offset = normalizedOffset,
                    total,
                    hasMore,
                    isError = result.IsError,
                    hits = result.Text,
                }, ToolJsonOptions);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Memory MCP recall failed for run {CorrelationId}.", context.CorrelationId);
                return JsonSerializer.Serialize(new
                {
                    available = true,
                    error = $"Recall failed: {exception.GetType().Name}",
                }, ToolJsonOptions);
            }
        }

        return AIFunctionFactory.Create(
            (Func<string, int, int, CancellationToken, Task<string>>)RecallMemoryAsync,
            name: "memory_recall",
            description: "Searches the user's durable long-term memory (Memory MCP, domain home: garden/seeds, pets, property, saved facts) by full-text relevance. Pass a natural-language query (e.g. 'pepper seed varieties'), not tag syntax. Args: query; topK (page size, default 4, up to 25); offset (skip N for pagination, default 0). The result includes `total` (the full match count — use it to answer 'how many', not the number of visible snippets) and `hasMore` (when true, call again with a larger offset to retrieve the rest). Returns matching notes or an empty result — if empty, say so; do not invent facts. Read-only.",
            serializerOptions: null);
    }

    private AIFunction CreateMemorySaveProposalTool(AgentContext context)
    {
        Task<ConfirmationProposalResult> ProposeMemorySaveAsync(
            string statement,
            string key,
            string tags,
            string summary,
            string risk,
            CancellationToken cancellationToken)
        {
            if (_confirmationService is null)
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("Confirmation service is not available."));
            }

            var normalizedStatement = NormalizeSingleLine(statement, maxLength: 600);
            if (string.IsNullOrWhiteSpace(normalizedStatement))
            {
                return Task.FromResult(ConfirmationProposalResult.Rejected("statement must be non-empty."));
            }

            var payloadJson = JsonSerializer.Serialize(new
            {
                statement = normalizedStatement,
                key = NormalizeSingleLine(key, maxLength: 80),
                tags = NormalizeSingleLine(tags, maxLength: 160),
            }, ToolJsonOptions);

            return _confirmationService.ProposeAsync(
                new ConfirmationProposalRequest(
                    context,
                    MemoryMcpSaveActionExecutor.MemoryMcpSaveActionKind,
                    $"memory_save:{context.ConversationKey}",
                    payloadJson,
                    string.IsNullOrWhiteSpace(summary)
                        ? $"Save durable memory: {Truncate(normalizedStatement, 80)}"
                        : NormalizeSingleLine(summary, maxLength: 200),
                    string.IsNullOrWhiteSpace(risk)
                        ? "A durable fact will be written to shared long-term memory (Memory MCP, domain home)."
                        : NormalizeSingleLine(risk, maxLength: 200)),
                cancellationToken);
        }

        return AIFunctionFactory.Create(
            (Func<string, string, string, string, string, CancellationToken, Task<ConfirmationProposalResult>>)ProposeMemorySaveAsync,
            name: "propose_memory_save",
            description: "Creates a pending confirmation to save a durable fact to long-term memory (Memory MCP). Arguments: statement, key (optional stable id), tags (comma-separated, optional), summary, risk. Requires explicit user approval via /approve.",
            serializerOptions: null);
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

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];
}
