using HaPersonalAgent.Configuration;
using HaPersonalAgent.Web;
using Microsoft.Extensions.Configuration;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты встроенного веб-хоста (HPA-025): опции, авторизация запроса, health-снимок и маппинг HA options.
/// Зачем: веб-слой за HA Ingress — runtime-контракт (порт, защита прямого доступа, отсутствие секретов в health).
/// Как: проверяет чистую логику без поднятия HTTP-сервера + binding snake_case опций add-on в WebHostOptions.
/// </summary>
public class WebHostTests
{
    [Fact]
    public void Web_host_options_defaults_enable_ui_on_ingress_port()
    {
        var options = new WebHostOptions();

        Assert.True(options.Enabled);
        Assert.Equal(8099, options.Port);
        Assert.Equal(WebHostOptions.DefaultPort, options.Port);
        Assert.Equal(string.Empty, options.ApiToken);
        Assert.False(options.IsApiTokenConfigured);
    }

    [Fact]
    public void Unconfigured_token_leaves_web_host_open()
    {
        Assert.True(WebRequestAuthorizer.IsAuthorized(hasIngressHeader: false, providedToken: null, configuredToken: null));
        Assert.True(WebRequestAuthorizer.IsAuthorized(hasIngressHeader: false, providedToken: null, configuredToken: "   "));
    }

    [Fact]
    public void Ingress_requests_are_trusted_even_without_token()
    {
        Assert.True(WebRequestAuthorizer.IsAuthorized(hasIngressHeader: true, providedToken: null, configuredToken: "secret-token"));
    }

    [Theory]
    [InlineData("secret-token", true)]
    [InlineData("wrong-token", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Direct_requests_require_matching_token(string? providedToken, bool expectedAuthorized)
    {
        var authorized = WebRequestAuthorizer.IsAuthorized(
            hasIngressHeader: false,
            providedToken: providedToken,
            configuredToken: "secret-token");

        Assert.Equal(expectedAuthorized, authorized);
    }

    [Fact]
    public void Health_snapshot_reports_version_without_secrets()
    {
        var snapshot = WebHealthSnapshot.Create(webUiEnabled: true);

        Assert.Equal("ok", snapshot.Status);
        Assert.Equal(ApplicationInfo.Name, snapshot.Application);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Version));
        Assert.Equal(ApplicationInfo.TargetFramework, snapshot.TargetFramework);
        Assert.True(snapshot.WebUiEnabled);
    }

    [Fact]
    public void Addon_options_mapper_binds_web_host_options()
    {
        const string json = """
            {
              "web_ui_enabled": false,
              "web_api_token": "web-secret"
            }
            """;

        var mapped = HomeAssistantAddOnOptionsMapper.MapJson(json);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(mapped)
            .Build();

        var options = new WebHostOptions();
        configuration.GetSection(WebHostOptions.SectionName).Bind(options);

        Assert.False(options.Enabled);
        Assert.Equal("web-secret", options.ApiToken);
        Assert.True(options.IsApiTokenConfigured);
    }

    [Fact]
    public void Environment_alias_overrides_web_api_token()
    {
        var environmentVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WEB_API_TOKEN"] = "env-web-secret",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Web:ApiToken"] = "old-web-secret",
            })
            .AddInMemoryCollection(EnvironmentOverridesMapper.Map(environmentVariables))
            .Build();

        var options = new WebHostOptions();
        configuration.GetSection(WebHostOptions.SectionName).Bind(options);

        Assert.Equal("env-web-secret", options.ApiToken);
    }
}
