using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.HomeAssistant;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    private const int ConversationMemorySearchDefaultTopK = 4;
    private const int ConversationMemorySearchMaxTopK = 8;
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
        Never claim a Home Assistant control action was executed until the app reports completion after user approval.
        Never reveal secrets or raw tokens.
        """;

    private readonly AgentStatusTool _statusTool;
    private readonly HomeAssistantMcpStatusTool? _homeAssistantMcpStatusTool;
    private readonly BoundedChatHistoryProvider? _boundedChatHistoryProvider;
    private readonly AgentStateRepository? _stateRepository;
    private readonly IConfirmationService? _confirmationService;
    private readonly ILogger<AgentToolCatalog> _logger;

    public AgentToolCatalog(
        AgentStatusTool statusTool,
        ILogger<AgentToolCatalog> logger,
        HomeAssistantMcpStatusTool? homeAssistantMcpStatusTool = null,
        BoundedChatHistoryProvider? boundedChatHistoryProvider = null,
        AgentStateRepository? stateRepository = null,
        IConfirmationService? confirmationService = null)
    {
        _statusTool = statusTool ?? throw new ArgumentNullException(nameof(statusTool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _homeAssistantMcpStatusTool = homeAssistantMcpStatusTool;
        _boundedChatHistoryProvider = boundedChatHistoryProvider;
        _stateRepository = stateRepository;
        _confirmationService = confirmationService;
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

        if (string.Equals(context.MemoryRetrievalMode, AgentOptions.MemoryRetrievalModeOnDemandTool, StringComparison.Ordinal)
            && _boundedChatHistoryProvider is not null)
        {
            tools.Add(CreateConversationMemorySearchTool(context));
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

        if (!executionPlan.UsesTools)
        {
            instructions.AppendLine();
            instructions.AppendLine(
                "This run uses a no-tools profile. Do not claim to inspect Home Assistant, call tools, read files, or control devices.");
            instructions.AppendLine(
                "If the user asks for live home state or control, explain that this requires the normal tool-enabled dialogue profile.");
            return instructions.ToString();
        }

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
            if (_boundedChatHistoryProvider is null)
            {
                instructions.AppendLine("On-demand memory search tool is unavailable in this runtime.");
            }
            else
            {
                instructions.AppendLine("Older vector memory is not auto-injected in this mode; call search_conversation_memory when historical facts are needed.");
            }
        }
        else
        {
            instructions.AppendLine("Older relevant vector memories are auto-injected as context before this run.");
        }

        instructions.AppendLine();
        instructions.AppendLine("Use project_capsules_list/project_capsule_get when you need long-term project facts from capsule memory.");

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

    private AIFunction CreateConversationMemorySearchTool(AgentContext context)
    {
        async Task<string> SearchConversationMemoryAsync(
            string query,
            int topK,
            CancellationToken cancellationToken)
        {
            if (_boundedChatHistoryProvider is null)
            {
                return JsonSerializer.Serialize(new
                {
                    available = false,
                    reason = "Bounded chat history provider is not registered.",
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

            if (string.IsNullOrWhiteSpace(query))
            {
                return JsonSerializer.Serialize(new
                {
                    available = true,
                    count = 0,
                    hits = Array.Empty<object>(),
                    reason = "query must be non-empty.",
                }, ToolJsonOptions);
            }

            var normalizedTopK = Math.Clamp(topK <= 0 ? ConversationMemorySearchDefaultTopK : topK, 1, ConversationMemorySearchMaxTopK);
            var hits = await _boundedChatHistoryProvider.SearchAsync(
                context.ConversationKey,
                query,
                normalizedTopK,
                cancellationToken);
            _logger.LogInformation(
                "On-demand conversation memory search for run {CorrelationId}: mode {MemoryRetrievalMode}, topK {TopK}, query length {QueryLength}, hits {HitCount}.",
                context.CorrelationId,
                context.MemoryRetrievalMode,
                normalizedTopK,
                query.Length,
                hits.Count);

            return JsonSerializer.Serialize(new
            {
                available = true,
                count = hits.Count,
                query = NormalizeSingleLine(query, maxLength: 240),
                topK = normalizedTopK,
                hits = hits.Select(hit => new
                {
                    sourceMessageId = hit.SourceMessageId,
                    role = hit.Role == AgentConversationRole.User ? "user" : "assistant",
                    score = Math.Round(hit.Score, 4),
                    text = NormalizeSingleLine(hit.Text, maxLength: 320),
                }),
            }, ToolJsonOptions);
        }

        return AIFunctionFactory.Create(
            (Func<string, int, CancellationToken, Task<string>>)SearchConversationMemoryAsync,
            name: "search_conversation_memory",
            description: "Searches older vector-overflow memory for this conversation. Use when memory retrieval mode is on_demand_tool.",
            serializerOptions: null);
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
                    contentMarkdown = capsule.ContentMarkdown,
                }),
            };

            return JsonSerializer.Serialize(payload, ToolJsonOptions);
        }

        return AIFunctionFactory.Create(
            (Func<CancellationToken, Task<string>>)ListProjectCapsulesAsync,
            name: "project_capsules_list",
            description: $"Returns up to {ProjectCapsuleToolListLimit} latest project capsules for this conversation as JSON.",
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
