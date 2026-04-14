namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: успешный результат чтения MCP metadata из Home Assistant.
/// Зачем: connector возвращает только обнаруженные tools/prompts, а интерпретация ошибок остается в health-слое.
/// Как: хранит безопасные описания MCP primitives без результатов выполнения tools и без секретов.
/// </summary>
public sealed record HomeAssistantMcpDiscovery(
    IReadOnlyList<HomeAssistantMcpItemInfo> Tools,
    IReadOnlyList<HomeAssistantMcpItemInfo> Prompts);
