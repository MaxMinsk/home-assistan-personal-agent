namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: безопасное описание MCP tool или prompt из Home Assistant.
/// Зачем: discovery должен показывать, что доступно агенту, но не выполнять tools и не тащить runtime results в память диалога.
/// Как: хранит только name/title/description, полученные из MCP metadata.
/// </summary>
public sealed record HomeAssistantMcpItemInfo(
    string Name,
    string? Title,
    string? Description);
