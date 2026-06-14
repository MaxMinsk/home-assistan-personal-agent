using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: диагностическая и защитная обертка над одним Home Assistant MCP tool.
/// Зачем: полный HA state или сырой search result может занимать тысячи токенов и повторно отправляться модели на каждом шаге function loop.
/// Как: следует MAF LoggingMcpTool pattern, логирует размер результата и заменяет чрезмерный payload валидным компактным JSON с началом/концом ответа.
/// Ссылка: https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentsWithFoundry/Agent_Step23_LocalMCP/Program.cs
/// </summary>
public sealed class McpToolResultGuardFunction : DelegatingAIFunction
{
    public const int MaximumResultChars = 12_000;
    private const int HeadPreviewChars = 8_000;
    private const int TailPreviewChars = 2_000;

    private readonly ILogger _logger;

    public McpToolResultGuardFunction(AIFunction innerFunction, ILogger logger)
        : base(innerFunction)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var result = await base.InvokeCoreAsync(arguments, cancellationToken);
        var serialized = SerializeResult(result);
        var estimatedTokens = EstimateTokens(serialized.Length);

        if (serialized.Length <= MaximumResultChars)
        {
            _logger.LogInformation(
                "Home Assistant MCP tool {ToolName} returned {ResultChars} chars (~{EstimatedTokens} tokens); result kept unchanged.",
                Name,
                serialized.Length,
                estimatedTokens);
            return result;
        }

        _logger.LogWarning(
            "Home Assistant MCP tool {ToolName} returned oversized result: {ResultChars} chars (~{EstimatedTokens} tokens); compacting to guarded preview before the next LLM tool step.",
            Name,
            serialized.Length,
            estimatedTokens);

        return JsonSerializer.SerializeToElement(new
        {
            truncated = true,
            reason = "MCP tool result exceeded the agent token-safety limit.",
            originalChars = serialized.Length,
            estimatedOriginalTokens = estimatedTokens,
            head = serialized[..Math.Min(HeadPreviewChars, serialized.Length)],
            tail = serialized[^Math.Min(TailPreviewChars, serialized.Length)..],
        });
    }

    private static string SerializeResult(object? result)
    {
        if (result is null)
        {
            return "null";
        }

        if (result is JsonElement element)
        {
            return element.GetRawText();
        }

        if (result is string text)
        {
            return text;
        }

        try
        {
            return JsonSerializer.Serialize(result);
        }
        catch (NotSupportedException)
        {
            return result.ToString() ?? string.Empty;
        }
    }

    private static int EstimateTokens(int chars) =>
        chars <= 0 ? 0 : (int)Math.Ceiling(chars / 4d);
}
