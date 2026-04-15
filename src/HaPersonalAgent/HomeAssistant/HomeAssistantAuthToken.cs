namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: результат выбора bearer token для Home Assistant API/MCP.
/// Зачем: add-on может ходить через Supervisor proxy с `SUPERVISOR_TOKEN`, а direct Core URL использует long-lived access token.
/// Как: хранит secret value только в Value, а ToString возвращает безопасное описание источника без самого токена.
/// </summary>
public sealed class HomeAssistantAuthToken
{
    private HomeAssistantAuthToken(
        bool isConfigured,
        string? value,
        string source,
        string? reason)
    {
        IsConfigured = isConfigured;
        Value = value;
        Source = source;
        Reason = reason;
    }

    public bool IsConfigured { get; }

    public string? Value { get; }

    public string Source { get; }

    public string? Reason { get; }

    public static HomeAssistantAuthToken Configured(string value, string source) =>
        new(
            isConfigured: true,
            value,
            source,
            reason: null);

    public static HomeAssistantAuthToken NotConfigured(string reason) =>
        new(
            isConfigured: false,
            value: null,
            source: "none",
            reason);

    public override string ToString() =>
        IsConfigured
            ? $"configured ({Source})"
            : $"not configured ({Reason})";
}
