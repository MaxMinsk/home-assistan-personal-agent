namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: safety-классификация MCP tool.
/// Зачем: агент должен понимать, какие Home Assistant tools можно выполнить сразу, а какие требуют будущего confirmation flow.
/// Как: policy возвращает ReadOnly для безопасных tools и RequiresConfirmation для всего, что может изменить состояние или неизвестно.
/// </summary>
public enum HomeAssistantMcpToolSafety
{
    ReadOnly,
    RequiresConfirmation,
}
