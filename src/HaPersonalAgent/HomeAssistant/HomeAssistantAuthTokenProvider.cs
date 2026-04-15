using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: выбор bearer token для Home Assistant Core/MCP.
/// Зачем: внутри add-on дефолтный URL `http://supervisor/core/...` требует `SUPERVISOR_TOKEN`, а внешний/direct Core URL требует long-lived access token пользователя.
/// Как: для host `supervisor` предпочитает env/config `SUPERVISOR_TOKEN`, иначе использует `HomeAssistant:LongLivedAccessToken`.
/// </summary>
public sealed class HomeAssistantAuthTokenProvider : IHomeAssistantAuthTokenProvider
{
    private const string SupervisorTokenKey = "SUPERVISOR_TOKEN";

    private readonly IConfiguration _configuration;
    private readonly IOptions<HomeAssistantOptions> _options;

    public HomeAssistantAuthTokenProvider(
        IOptions<HomeAssistantOptions> options,
        IConfiguration configuration)
    {
        _options = options;
        _configuration = configuration;
    }

    public HomeAssistantAuthToken Resolve(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var supervisorToken = _configuration[SupervisorTokenKey];
        if (IsSupervisorProxyEndpoint(endpoint)
            && !string.IsNullOrWhiteSpace(supervisorToken))
        {
            return HomeAssistantAuthToken.Configured(
                supervisorToken.Trim(),
                SupervisorTokenKey);
        }

        var longLivedToken = _options.Value.LongLivedAccessToken;
        if (!string.IsNullOrWhiteSpace(longLivedToken))
        {
            return HomeAssistantAuthToken.Configured(
                longLivedToken.Trim(),
                "HomeAssistant:LongLivedAccessToken");
        }

        return IsSupervisorProxyEndpoint(endpoint)
            ? HomeAssistantAuthToken.NotConfigured(
                "SUPERVISOR_TOKEN is missing for http://supervisor/core endpoint and HomeAssistant:LongLivedAccessToken is empty.")
            : HomeAssistantAuthToken.NotConfigured(
                "HomeAssistant:LongLivedAccessToken is empty.");
    }

    private static bool IsSupervisorProxyEndpoint(Uri endpoint) =>
        string.Equals(endpoint.Host, "supervisor", StringComparison.OrdinalIgnoreCase)
        && endpoint.AbsolutePath.StartsWith("/core/", StringComparison.OrdinalIgnoreCase);
}
