namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: безопасный статус discovery Home Assistant MCP endpoint.
/// Зачем: `/status` должен показывать configured/reachable/auth state без вывода Home Assistant token.
/// Как: фабричные методы собирают короткий статус, endpoint URL и счетчики tools/prompts, не сохраняя секреты.
/// </summary>
public sealed record HomeAssistantMcpDiscoveryResult(
    HomeAssistantMcpStatus Status,
    string EndpointUrl,
    bool TokenConfigured,
    string? Reason,
    int ToolCount,
    int PromptCount,
    IReadOnlyList<HomeAssistantMcpItemInfo> Tools,
    IReadOnlyList<HomeAssistantMcpItemInfo> Prompts)
{
    private static readonly IReadOnlyList<HomeAssistantMcpItemInfo> EmptyItems =
        Array.Empty<HomeAssistantMcpItemInfo>();

    public static HomeAssistantMcpDiscoveryResult InvalidConfiguration(string reason) =>
        new(
            HomeAssistantMcpStatus.InvalidConfiguration,
            EndpointUrl: "",
            TokenConfigured: false,
            reason,
            ToolCount: 0,
            PromptCount: 0,
            EmptyItems,
            EmptyItems);

    public static HomeAssistantMcpDiscoveryResult NotConfigured(Uri endpoint, string reason) =>
        new(
            HomeAssistantMcpStatus.NotConfigured,
            endpoint.ToString(),
            TokenConfigured: false,
            reason,
            ToolCount: 0,
            PromptCount: 0,
            EmptyItems,
            EmptyItems);

    public static HomeAssistantMcpDiscoveryResult Reachable(Uri endpoint, HomeAssistantMcpDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        return new HomeAssistantMcpDiscoveryResult(
            HomeAssistantMcpStatus.Reachable,
            endpoint.ToString(),
            TokenConfigured: true,
            Reason: null,
            discovery.Tools.Count,
            discovery.Prompts.Count,
            discovery.Tools,
            discovery.Prompts);
    }

    public static HomeAssistantMcpDiscoveryResult Failed(
        HomeAssistantMcpStatus status,
        Uri endpoint,
        string reason) =>
        new(
            status,
            endpoint.ToString(),
            TokenConfigured: true,
            reason,
            ToolCount: 0,
            PromptCount: 0,
            EmptyItems,
            EmptyItems);
}
