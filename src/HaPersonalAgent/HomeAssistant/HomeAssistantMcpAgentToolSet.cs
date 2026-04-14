using Microsoft.Extensions.AI;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: disposable набор MCP tools для одного agent run.
/// Зачем: MCP tools завязаны на открытую session, поэтому их lifetime должен быть явно привязан к выполнению агента.
/// Как: содержит exposed read-only AIFunction tools, metadata tools для confirmation proposal и optional session, которую DisposeAsync закрывает после run.
/// </summary>
public sealed class HomeAssistantMcpAgentToolSet : IAsyncDisposable
{
    private readonly IAsyncDisposable? _ownsSession;

    private HomeAssistantMcpAgentToolSet(
        HomeAssistantMcpStatus status,
        string? reason,
        IReadOnlyList<AIFunction> tools,
        IReadOnlyList<HomeAssistantMcpItemInfo> confirmationRequiredTools,
        int totalToolCount,
        IAsyncDisposable? ownsSession)
    {
        Status = status;
        Reason = reason;
        Tools = tools;
        ConfirmationRequiredTools = confirmationRequiredTools;
        TotalToolCount = totalToolCount;
        _ownsSession = ownsSession;
    }

    public HomeAssistantMcpStatus Status { get; }

    public string? Reason { get; }

    public IReadOnlyList<AIFunction> Tools { get; }

    public IReadOnlyList<HomeAssistantMcpItemInfo> ConfirmationRequiredTools { get; }

    public int TotalToolCount { get; }

    public int ExposedToolCount => Tools.Count;

    public int BlockedToolCount => Math.Max(0, TotalToolCount - ExposedToolCount);

    public static HomeAssistantMcpAgentToolSet Available(
        IReadOnlyList<AIFunction> tools,
        IReadOnlyList<HomeAssistantMcpItemInfo> confirmationRequiredTools,
        int totalToolCount,
        IAsyncDisposable? ownsSession) =>
        new(
            HomeAssistantMcpStatus.Reachable,
            reason: null,
            tools,
            confirmationRequiredTools,
            totalToolCount,
            ownsSession);

    public static HomeAssistantMcpAgentToolSet Unavailable(
        HomeAssistantMcpStatus status,
        string reason) =>
        new(
            status,
            reason,
            Array.Empty<AIFunction>(),
            Array.Empty<HomeAssistantMcpItemInfo>(),
            totalToolCount: 0,
            ownsSession: null);

    public async ValueTask DisposeAsync()
    {
        if (_ownsSession is not null)
        {
            await _ownsSession.DisposeAsync();
        }
    }
}
