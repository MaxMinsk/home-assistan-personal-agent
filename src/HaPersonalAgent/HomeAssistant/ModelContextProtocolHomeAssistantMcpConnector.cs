using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.HomeAssistant;

/// <summary>
/// Что: connector Home Assistant MCP на официальном ModelContextProtocol SDK.
/// Зачем: нам нужен реальный Streamable HTTP transport к `/api/mcp`, но application слой не должен зависеть от деталей SDK.
/// Как: создает HttpClientTransport с Bearer token, открывает MCP session, читает metadata или возвращает живую session для agent tools.
/// </summary>
public sealed class ModelContextProtocolHomeAssistantMcpConnector :
    IHomeAssistantMcpConnector,
    IHomeAssistantMcpToolConnector
{
    public const string HttpClientName = "HomeAssistantMcp";

    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public ModelContextProtocolHomeAssistantMcpConnector(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public async Task<HomeAssistantMcpDiscovery> DiscoverAsync(
        Uri endpoint,
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access token is required.", nameof(accessToken));
        }

        await using var session = await OpenSessionAsync(endpoint, accessToken, cancellationToken);

        var prompts = await session.Client.ListPromptsAsync(cancellationToken: cancellationToken);

        return new HomeAssistantMcpDiscovery(
            session.Tools.Select(tool => new HomeAssistantMcpItemInfo(tool.Name, tool.Title, tool.Description)).ToArray(),
            prompts.Select(prompt => new HomeAssistantMcpItemInfo(prompt.Name, prompt.Title, prompt.Description)).ToArray());
    }

    public async Task<HomeAssistantMcpToolSession> ConnectToolsAsync(
        Uri endpoint,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var session = await OpenSessionAsync(endpoint, accessToken, cancellationToken);

        return new HomeAssistantMcpToolSession(
            session.Tools,
            session.DisposeAsync);
    }

    public HttpClientTransportOptions CreateTransportOptions(Uri endpoint, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access token is required.", nameof(accessToken));
        }

        return new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "Home Assistant MCP",
            ConnectionTimeout = ConnectionTimeout,
            AdditionalHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {accessToken.Trim()}",
            },
        };
    }

    private static McpClientOptions CreateClientOptions() =>
        new()
        {
            ClientInfo = new Implementation
            {
                Name = ApplicationInfo.Name,
                Version = ApplicationInfo.Version,
            },
            InitializationTimeout = ConnectionTimeout,
        };

    private async Task<SdkToolSession> OpenSessionAsync(
        Uri endpoint,
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access token is required.", nameof(accessToken));
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        var transportOptions = CreateTransportOptions(endpoint, accessToken);
        var transport = new HttpClientTransport(
            transportOptions,
            httpClient,
            _loggerFactory,
            ownsHttpClient: false);

        try
        {
            var clientOptions = CreateClientOptions();
            var client = await McpClient.CreateAsync(
                transport,
                clientOptions,
                _loggerFactory,
                cancellationToken);

            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

                return new SdkToolSession(
                    httpClient,
                    transport,
                    client,
                    tools.ToArray());
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await transport.DisposeAsync();
            httpClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Что: private holder открытой SDK session.
    /// Зачем: discovery и agent tool provider используют одинаковую логику подключения, но по-разному управляют lifetime.
    /// Как: DisposeAsync закрывает McpClient, transport и HttpClient, созданные для этой session.
    /// </summary>
    private sealed class SdkToolSession : IAsyncDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly McpClient _client;
        private readonly HttpClientTransport _transport;

        public SdkToolSession(
            HttpClient httpClient,
            HttpClientTransport transport,
            McpClient client,
            IReadOnlyList<McpClientTool> tools)
        {
            _httpClient = httpClient;
            _transport = transport;
            _client = client;
            Tools = tools;
        }

        public McpClient Client => _client;

        public IReadOnlyList<McpClientTool> Tools { get; }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _transport.DisposeAsync();
            _httpClient.Dispose();
        }
    }
}
