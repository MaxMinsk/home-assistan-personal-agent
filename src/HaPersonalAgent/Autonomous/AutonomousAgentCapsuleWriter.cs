using System.Globalization;
using System.Text;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: дисциплинированная запись результатов фонового запуска в Memory MCP (HPA-031).
/// Зачем: ключевое требование эпика — «использовать память, но не засорять её»: журнал запусков остаётся локальным,
/// а в общую память идёт ОДНА идемпотентная research-капсула на агента плюс жёстко ограниченное число durable-фактов.
/// Как: notes_upsert по стабильному dedupKey (hpa-agent-capsule-&lt;agentId&gt;) переписывает ту же заметку каждый запуск —
/// заметок не становится больше со временем; факты пишутся отдельными нотами с dedupKey по слагу, не более лимита за запуск.
/// </summary>
public sealed class AutonomousAgentCapsuleWriter
{
    public const string CapsuleNoteType = "project_state";
    public const string FactNoteType = "fact";

    private const int MaxSlugLength = 48;
    private const int MaxTitleLength = 80;
    private const int MaxStateLength = 4_000;

    private readonly IMemoryMcpClient _memoryClient;
    private readonly IOptions<MemoryMcpOptions> _memoryOptions;
    private readonly ILogger<AutonomousAgentCapsuleWriter> _logger;

    public AutonomousAgentCapsuleWriter(
        IMemoryMcpClient memoryClient,
        IOptions<MemoryMcpOptions> memoryOptions,
        ILogger<AutonomousAgentCapsuleWriter> logger)
    {
        _memoryClient = memoryClient;
        _memoryOptions = memoryOptions;
        _logger = logger;
    }

    public static string BuildCapsuleDedupKey(string agentId) => $"hpa-agent-capsule-{agentId}";

    /// <summary>
    /// Публикует состояние исследования агента. Возвращает dedupKey капсулы при успехе и null,
    /// когда запись отключена политикой агента или Memory MCP не настроен.
    /// </summary>
    public async Task<string?> PublishAsync(
        AutonomousAgentDefinition definition,
        AutonomousRunOutput output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(output);

        if (!definition.ToolScope.AllowMemoryWrite)
        {
            _logger.LogInformation(
                "Autonomous agent {AgentId} has memory write disabled; nothing was published to Memory MCP.",
                definition.Id);
            return null;
        }

        if (!_memoryOptions.Value.IsConfigured)
        {
            _logger.LogInformation(
                "Memory MCP is not configured; autonomous agent {AgentId} kept its results local only.",
                definition.Id);
            return null;
        }

        var capsuleDedupKey = BuildCapsuleDedupKey(definition.Id);
        if (!await UpsertCapsuleAsync(definition, output, capsuleDedupKey, cancellationToken))
        {
            return null;
        }

        await UpsertDurableFactsAsync(definition, output, cancellationToken);
        return capsuleDedupKey;
    }

    private async Task<bool> UpsertCapsuleAsync(
        AutonomousAgentDefinition definition,
        AutonomousRunOutput output,
        string capsuleDedupKey,
        CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["domain"] = MemoryMcpSaveActionExecutor.MemoryDomain,
            ["type"] = CapsuleNoteType,
            ["dedupKey"] = capsuleDedupKey,
            ["title"] = Truncate($"Автономный агент: {definition.Name}", MaxTitleLength),
            ["body"] = BuildCapsuleBody(definition, output),
            ["sourceAgent"] = ApplicationInfo.Name,
            ["tags"] = new[] { "ha-personal-agent", "autonomous-agent", "research-capsule" },
            ["payload"] = new Dictionary<string, object?>
            {
                ["project"] = BuildProjectSlug(definition),
                ["state"] = Truncate(BuildStateText(output), MaxStateLength),
                ["open_issues"] = output.Questions.ToArray(),
                ["updated"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            },
        };

        try
        {
            var result = await _memoryClient.CallToolAsync("notes_upsert", arguments, cancellationToken);
            if (result.IsError)
            {
                _logger.LogWarning(
                    "Memory MCP rejected the research capsule for autonomous agent {AgentId}: {Detail}",
                    definition.Id,
                    result.Text);
                return false;
            }

            _logger.LogInformation(
                "Research capsule {DedupKey} updated for autonomous agent {AgentId} (one note per agent, rewritten every run).",
                capsuleDedupKey,
                definition.Id);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Сбой памяти не должен ронять запуск: сводка уже сохранена локально и уйдёт пользователю.
            _logger.LogWarning(
                exception,
                "Failed to publish the research capsule for autonomous agent {AgentId}.",
                definition.Id);
            return false;
        }
    }

    private async Task UpsertDurableFactsAsync(
        AutonomousAgentDefinition definition,
        AutonomousRunOutput output,
        CancellationToken cancellationToken)
    {
        var budget = Math.Min(output.DurableFacts.Count, definition.ToolScope.MaxDurableFactsPerRun);
        if (budget <= 0)
        {
            return;
        }

        for (var index = 0; index < budget; index++)
        {
            var fact = output.DurableFacts[index];
            var dedupKey = $"hpa-agent-fact-{definition.Id}-{BuildSlug(fact)}";

            try
            {
                var result = await _memoryClient.CallToolAsync(
                    "notes_upsert",
                    new Dictionary<string, object?>
                    {
                        ["domain"] = MemoryMcpSaveActionExecutor.MemoryDomain,
                        ["type"] = FactNoteType,
                        ["dedupKey"] = dedupKey,
                        ["title"] = Truncate(fact, MaxTitleLength),
                        ["body"] = fact,
                        ["sourceAgent"] = ApplicationInfo.Name,
                        ["tags"] = new[] { "ha-personal-agent", "autonomous-agent" },
                        ["payload"] = new Dictionary<string, object?>
                        {
                            ["statement"] = fact,
                            ["source"] = $"ha-personal-agent (autonomous agent '{definition.Name}')",
                            ["as_of"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        },
                    },
                    cancellationToken);

                if (result.IsError)
                {
                    _logger.LogWarning(
                        "Memory MCP rejected a durable fact from autonomous agent {AgentId}: {Detail}",
                        definition.Id,
                        result.Text);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to publish a durable fact from autonomous agent {AgentId}.",
                    definition.Id);
            }
        }

        _logger.LogInformation(
            "Autonomous agent {AgentId} published {FactCount} durable fact(s) (budget {Budget} per run).",
            definition.Id,
            budget,
            definition.ToolScope.MaxDurableFactsPerRun);
    }

    private static string BuildCapsuleBody(AutonomousAgentDefinition definition, AutonomousRunOutput output)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {definition.Name}");
        builder.AppendLine();
        builder.AppendLine("## Миссия");
        builder.AppendLine(definition.Mission);
        builder.AppendLine();
        builder.AppendLine("## Текущее состояние исследования");
        builder.AppendLine(BuildStateText(output));

        if (output.Questions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Открытые вопросы к владельцу");
            foreach (var question in output.Questions)
            {
                builder.AppendLine($"- {question}");
            }
        }

        if (!string.IsNullOrWhiteSpace(output.NextFocus))
        {
            builder.AppendLine();
            builder.AppendLine("## Следующий шаг");
            builder.AppendLine(output.NextFocus);
        }

        return builder.ToString();
    }

    /// <summary>Сводка теперь — лишь рамка, суть в findings; в состояние капсулы кладём и то, и другое.</summary>
    private static string BuildStateText(AutonomousRunOutput output)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(output.Summary))
        {
            builder.AppendLine(output.Summary.Trim());
        }

        foreach (var finding in output.Findings)
        {
            builder.AppendLine($"- {finding}");
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? "Запуск не дал результата." : text;
    }

    private static string BuildProjectSlug(AutonomousAgentDefinition definition)
    {
        var slug = BuildSlug(definition.Name);
        return string.IsNullOrWhiteSpace(slug) ? $"hpa-agent-{definition.Id}" : $"hpa-agent-{slug}";
    }

    private static string BuildSlug(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= MaxSlugLength)
            {
                break;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
