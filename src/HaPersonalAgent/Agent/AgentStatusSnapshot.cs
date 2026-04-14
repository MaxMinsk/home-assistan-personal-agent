using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: результат безопасного status tool.
/// Зачем: модель и будущая команда /status должны получать версию, uptime и режим конфигурации без доступа к секретам.
/// Как: record сериализуется MAF function tool как structured result и включает уже замаскированный ConfigurationStatus.
/// </summary>
public sealed record AgentStatusSnapshot(
    string ApplicationName,
    string Version,
    string TargetFramework,
    string Uptime,
    string ConfigurationMode,
    ConfigurationStatus Configuration);
