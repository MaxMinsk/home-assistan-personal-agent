using HaPersonalAgent.Configuration;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: безопасный tool status для Microsoft Agent Framework.
/// Зачем: это первый учебный tool, на котором проверяем function calling без сайд-эффектов и без раскрытия секретов.
/// Как: собирает ApplicationInfo, uptime процесса и ConfigurationStatusProvider, а затем возвращает сериализуемый AgentStatusSnapshot.
/// </summary>
public sealed class AgentStatusTool
{
    private readonly ConfigurationStatusProvider _configurationStatusProvider;
    private readonly LlmRoutingTelemetry _routingTelemetry;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    public AgentStatusTool(
        ConfigurationStatusProvider configurationStatusProvider,
        LlmRoutingTelemetry? routingTelemetry = null)
    {
        _configurationStatusProvider = configurationStatusProvider;
        _routingTelemetry = routingTelemetry ?? new LlmRoutingTelemetry();
    }

    public AgentStatusSnapshot GetStatus()
    {
        var uptime = DateTimeOffset.UtcNow - _startedAtUtc;

        return new AgentStatusSnapshot(
            ApplicationInfo.Name,
            ApplicationInfo.Version,
            ApplicationInfo.TargetFramework,
            FormatUptime(uptime),
            DetectConfigurationMode(),
            _configurationStatusProvider.Create(),
            _routingTelemetry.Snapshot());
    }

    private static string DetectConfigurationMode() =>
        File.Exists(ConfigurationBuilderExtensions.DefaultHomeAssistantAddOnOptionsPath)
            ? "home-assistant-add-on"
            : "local";

    private static string FormatUptime(TimeSpan uptime) =>
        $"{(int)uptime.TotalDays}d {uptime:hh\\:mm\\:ss}";
}
