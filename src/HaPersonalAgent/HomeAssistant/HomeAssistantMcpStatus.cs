namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: нормализованные состояния Home Assistant MCP discovery.
/// Зачем: UI/status и тесты должны отличать missing token, auth failed, missing integration и общую сетевую ошибку.
/// Как: HomeAssistantMcpClient мапит исключения SDK/HTTP в эти значения без раскрытия деталей токена.
/// </summary>
public enum HomeAssistantMcpStatus
{
    NotConfigured,
    InvalidConfiguration,
    Reachable,
    AuthFailed,
    IntegrationMissing,
    Unreachable,
    Error,
}
