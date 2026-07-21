using System.Text;
using System.Text.Json;
using HaPersonalAgent.Agent;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: LLM-реализация поиска связей для сводного дайджеста (HPA-039, часть B).
/// Зачем: блок «Связи» обязан быть заземлён — никаких выдуманных пересечений. Поэтому это отдельный узкий проход
/// с жёстким контрактом: перечисляй только связи, прямо следующие из находок, иначе пустой список.
/// Как: строит промпт из сводок/находок агентов, зовёт runtime без инструментов (PureChat), парсит строгий JSON.
/// Любой сбой (не настроен рантайм, не распарсилось) => пустой список: дайджест всё равно уходит, просто без связей.
/// </summary>
public sealed class AutonomousConnectionFinder : IAutonomousConnectionFinder
{
    private const int MaxConnections = 5;

    private readonly IAgentRuntime _runtime;
    private readonly ILogger<AutonomousConnectionFinder> _logger;

    public AutonomousConnectionFinder(
        IAgentRuntime runtime,
        ILogger<AutonomousConnectionFinder> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> FindConnectionsAsync(
        IReadOnlyList<AutonomousRunDelivery> deliveries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        if (deliveries.Count < 2)
        {
            return Array.Empty<string>();
        }

        try
        {
            var response = await _runtime.SendAsync(
                BuildPrompt(deliveries),
                AgentContext.Create(
                    correlationId: $"digest-connections-{deliveries.Count}",
                    executionProfile: LlmExecutionProfile.PureChat),
                onReasoningUpdate: null,
                cancellationToken);

            if (!response.IsConfigured || string.IsNullOrWhiteSpace(response.Text))
            {
                return Array.Empty<string>();
            }

            return ParseConnections(response.Text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to compute cross-agent connections; the digest will omit the connections block.");
            return Array.Empty<string>();
        }
    }

    private static string BuildPrompt(IReadOnlyList<AutonomousRunDelivery> deliveries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Ниже — результаты нескольких фоновых агентов за одно окно. Найди СВЯЗИ между их находками —");
        builder.AppendLine("только те, что ПРЯМО подтверждаются приведённым текстом. Никогда не выдумывай связь. Если явных");
        builder.AppendLine("связей нет — верни пустой список. Каждая связь — короткая фраза, называющая оба агента.");
        builder.AppendLine();

        foreach (var delivery in deliveries)
        {
            builder.AppendLine($"Агент «{delivery.Definition.Name}»:");
            if (!string.IsNullOrWhiteSpace(delivery.Output.Summary))
            {
                builder.AppendLine(delivery.Output.Summary.Trim());
            }

            foreach (var finding in delivery.Output.Findings)
            {
                builder.AppendLine($"- {finding}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("Ответь ТОЛЬКО этим JSON-объектом, без пояснений:");
        builder.AppendLine("""{ "connections": ["связь 1", "связь 2"] }""");
        return builder.ToString();
    }

    private static IReadOnlyList<string> ParseConnections(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            if (!document.RootElement.TryGetProperty("connections", out var connections)
                || connections.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>();
            foreach (var item in connections.EnumerateArray())
            {
                if (result.Count >= MaxConnections)
                {
                    break;
                }

                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value.Trim());
                    }
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
