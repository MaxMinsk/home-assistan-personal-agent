using System.Net;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.HomeAssistant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты подключения к Home Assistant MCP без настоящего Home Assistant.
/// Зачем: важно проверить URL, bearer auth и graceful degradation до запуска add-on на домашнем сервере.
/// Как: endpoint builder и transport options проверяются напрямую, а MCP discovery имитируется fake connector.
/// </summary>
public class HomeAssistantMcpTests
{
    [Theory]
    [InlineData("http://supervisor/core", "/api/mcp", "http://supervisor/core/api/mcp")]
    [InlineData("http://supervisor/core/", "api/mcp", "http://supervisor/core/api/mcp")]
    [InlineData("http://homeassistant.local:8123", "https://external.example/api/mcp", "https://external.example/api/mcp")]
    public void Endpoint_builder_composes_mcp_endpoint(
        string homeAssistantUrl,
        string mcpEndpoint,
        string expectedEndpoint)
    {
        var built = HomeAssistantMcpEndpointBuilder.TryBuild(
            homeAssistantUrl,
            mcpEndpoint,
            out var endpoint,
            out var reason);

        Assert.True(built, reason);
        Assert.Equal(expectedEndpoint, endpoint?.ToString());
    }

    [Fact]
    public void Sdk_connector_configures_streamable_http_and_bearer_header()
    {
        var connector = new ModelContextProtocolHomeAssistantMcpConnector(
            new FakeHttpClientFactory(),
            LoggerFactory.Create(_ => { }));

        var transportOptions = connector.CreateTransportOptions(
            new Uri("http://supervisor/core/api/mcp"),
            "ha-secret");

        Assert.Equal(new Uri("http://supervisor/core/api/mcp"), transportOptions.Endpoint);
        Assert.Equal(ModelContextProtocol.Client.HttpTransportMode.StreamableHttp, transportOptions.TransportMode);
        Assert.NotNull(transportOptions.AdditionalHeaders);
        Assert.Equal("Bearer ha-secret", transportOptions.AdditionalHeaders["Authorization"]);
    }

    [Fact]
    public async Task Discovery_reports_not_configured_without_token_and_does_not_call_connector()
    {
        var connector = new FakeMcpConnector();
        var client = CreateClient(
            new HomeAssistantOptions
            {
                LongLivedAccessToken = "",
            },
            connector);

        var result = await client.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HomeAssistantMcpStatus.NotConfigured, result.Status);
        Assert.False(result.TokenConfigured);
        Assert.False(connector.WasCalled);
        Assert.Contains("LongLivedAccessToken", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_returns_reachable_with_tools_and_prompts()
    {
        var client = CreateClient(
            new HomeAssistantOptions
            {
                LongLivedAccessToken = "ha-secret",
            },
            new FakeMcpConnector(
                new HomeAssistantMcpDiscovery(
                    new[] { new HomeAssistantMcpItemInfo("get_state", "Get state", "Reads HA state") },
                    new[] { new HomeAssistantMcpItemInfo("assist", "Assist", "Uses Assist API") })));

        var result = await client.DiscoverAsync(CancellationToken.None);

        Assert.Equal(HomeAssistantMcpStatus.Reachable, result.Status);
        Assert.True(result.TokenConfigured);
        Assert.Equal(1, result.ToolCount);
        Assert.Equal(1, result.PromptCount);
        Assert.DoesNotContain("ha-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, HomeAssistantMcpStatus.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, HomeAssistantMcpStatus.AuthFailed)]
    [InlineData(HttpStatusCode.NotFound, HomeAssistantMcpStatus.IntegrationMissing)]
    public async Task Discovery_maps_http_errors_to_safe_statuses(
        HttpStatusCode statusCode,
        HomeAssistantMcpStatus expectedStatus)
    {
        var client = CreateClient(
            new HomeAssistantOptions
            {
                LongLivedAccessToken = "ha-secret",
            },
            new FakeMcpConnector(new HttpRequestException("HTTP error", null, statusCode)));

        var result = await client.DiscoverAsync(CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.DoesNotContain("ha-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HassGetState", "Reads entity state", HomeAssistantMcpToolSafety.ReadOnly)]
    [InlineData("HassListEntities", "Lists exposed entities", HomeAssistantMcpToolSafety.ReadOnly)]
    [InlineData("HassCallService", "Calls a Home Assistant service", HomeAssistantMcpToolSafety.RequiresConfirmation)]
    [InlineData("HassTurnOn", "Turns on an entity", HomeAssistantMcpToolSafety.RequiresConfirmation)]
    [InlineData("HassUnknown", "Does something not documented", HomeAssistantMcpToolSafety.RequiresConfirmation)]
    public void Tool_policy_exposes_only_read_only_tools(
        string toolName,
        string description,
        HomeAssistantMcpToolSafety expectedSafety)
    {
        var policy = new HomeAssistantMcpToolPolicy();

        var safety = policy.Classify(toolName, description);

        Assert.Equal(expectedSafety, safety);
    }

    [Fact]
    public async Task Agent_tool_provider_exposes_read_only_tools_and_disposes_session_after_run()
    {
        var connector = new FakeMcpToolConnector(
            CreateTool("HassGetState", "Reads entity state"),
            CreateTool("HassCallService", "Calls a Home Assistant service"),
            CreateTool("HassUnknown", "Does something not documented"));
        var provider = CreateToolProvider(
            new HomeAssistantOptions
            {
                LongLivedAccessToken = "ha-secret",
            },
            connector);

        await using var toolSet = await provider.CreateReadOnlyToolSetAsync(CancellationToken.None);

        Assert.Equal(HomeAssistantMcpStatus.Reachable, toolSet.Status);
        Assert.Equal(3, toolSet.TotalToolCount);
        Assert.Equal(1, toolSet.ExposedToolCount);
        Assert.Equal(2, toolSet.BlockedToolCount);
        Assert.Equal("HassGetState", toolSet.Tools.Single().Name);
        Assert.Equal(new[] { "HassCallService", "HassUnknown" }, toolSet.ConfirmationRequiredTools.Select(tool => tool.Name));
        Assert.True(connector.WasCalled);
        Assert.False(connector.SessionDisposed);

        await toolSet.DisposeAsync();

        Assert.True(connector.SessionDisposed);
    }

    [Fact]
    public async Task Agent_tool_provider_does_not_connect_without_token()
    {
        var connector = new FakeMcpToolConnector(CreateTool("HassGetState", "Reads entity state"));
        var provider = CreateToolProvider(
            new HomeAssistantOptions
            {
                LongLivedAccessToken = "",
            },
            connector);

        var toolSet = await provider.CreateReadOnlyToolSetAsync(CancellationToken.None);

        Assert.Equal(HomeAssistantMcpStatus.NotConfigured, toolSet.Status);
        Assert.Empty(toolSet.Tools);
        Assert.False(connector.WasCalled);
    }

    private static HomeAssistantMcpClient CreateClient(
        HomeAssistantOptions options,
        IHomeAssistantMcpConnector connector) =>
        new(
            Options.Create(options),
            connector,
            LoggerFactory.Create(_ => { }).CreateLogger<HomeAssistantMcpClient>());

    private static HomeAssistantMcpAgentToolProvider CreateToolProvider(
        HomeAssistantOptions options,
        IHomeAssistantMcpToolConnector connector) =>
        new(
            Options.Create(options),
            connector,
            new HomeAssistantMcpToolPolicy(),
            LoggerFactory.Create(_ => { }).CreateLogger<HomeAssistantMcpAgentToolProvider>());

    private static AIFunction CreateTool(string name, string description) =>
        AIFunctionFactory.Create(
            (Func<string>)(() => "ok"),
            name,
            description,
            serializerOptions: null);

    /// <summary>
    /// Что: fake IHttpClientFactory для проверки transport options.
    /// Зачем: SDK connector требует фабрику, но тест bearer header не должен выполнять HTTP.
    /// Как: возвращает новый HttpClient с дефолтным handler.
    /// </summary>
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// Что: fake MCP connector для unit-тестов health/discovery слоя.
    /// Зачем: тесты должны проверять mapping ошибок без реального Home Assistant MCP Server.
    /// Как: возвращает заданный discovery result или бросает заданное исключение.
    /// </summary>
    private sealed class FakeMcpConnector : IHomeAssistantMcpConnector
    {
        private readonly HomeAssistantMcpDiscovery _discovery;
        private readonly Exception? _exception;

        public FakeMcpConnector()
            : this(new HomeAssistantMcpDiscovery(Array.Empty<HomeAssistantMcpItemInfo>(), Array.Empty<HomeAssistantMcpItemInfo>()))
        {
        }

        public FakeMcpConnector(HomeAssistantMcpDiscovery discovery)
        {
            _discovery = discovery;
        }

        public FakeMcpConnector(Exception exception)
            : this()
        {
            _exception = exception;
        }

        public bool WasCalled { get; private set; }

        public Task<HomeAssistantMcpDiscovery> DiscoverAsync(
            Uri endpoint,
            string accessToken,
            CancellationToken cancellationToken)
        {
            WasCalled = true;

            return _exception is null
                ? Task.FromResult(_discovery)
                : Task.FromException<HomeAssistantMcpDiscovery>(_exception);
        }
    }

    /// <summary>
    /// Что: fake connector для agent-facing MCP tools.
    /// Зачем: проверяем filtering/lifetime без настоящей MCP session.
    /// Как: возвращает заранее заданные AIFunction tools и фиксирует DisposeAsync session.
    /// </summary>
    private sealed class FakeMcpToolConnector : IHomeAssistantMcpToolConnector
    {
        private readonly IReadOnlyList<AIFunction> _tools;

        public FakeMcpToolConnector(params AIFunction[] tools)
        {
            _tools = tools;
        }

        public bool WasCalled { get; private set; }

        public bool SessionDisposed { get; private set; }

        public Task<HomeAssistantMcpToolSession> ConnectToolsAsync(
            Uri endpoint,
            string accessToken,
            CancellationToken cancellationToken)
        {
            WasCalled = true;

            return Task.FromResult(new HomeAssistantMcpToolSession(
                _tools,
                () =>
                {
                    SessionDisposed = true;
                    return ValueTask.CompletedTask;
                }));
        }
    }
}
