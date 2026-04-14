using Microsoft.Extensions.AI;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: открытая MCP session с invocable tools.
/// Зачем: McpClientTool вызывает сервер через живой MCP client, поэтому session должна жить до завершения agent run.
/// Как: хранит AIFunction tools и async dispose callback, который закрывает MCP client/transport/http ресурсы.
/// </summary>
public sealed class HomeAssistantMcpToolSession : IAsyncDisposable
{
    private readonly Func<ValueTask> _disposeAsync;

    public HomeAssistantMcpToolSession(
        IReadOnlyList<AIFunction> tools,
        Func<ValueTask> disposeAsync)
    {
        Tools = tools;
        _disposeAsync = disposeAsync;
    }

    public IReadOnlyList<AIFunction> Tools { get; }

    public ValueTask DisposeAsync() => _disposeAsync();
}
