using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace HaPersonalAgent.Memory;

/// <summary>
/// What: Memory MCP connector on the official ModelContextProtocol SDK (streamable HTTP + bearer token).
/// Why: HPA-003 reuses the same SDK transport stack as the Home Assistant MCP client, pointed at the
/// Memory MCP endpoint, so the app can probe health and call tools without leaking SDK details upward.
/// How: builds an HttpClientTransport with the bearer header, opens a short-lived McpClient session per
/// operation, reads ServerInfo/tool count for discovery, and maps tool-call content to a plain result.
/// </summary>
public sealed class ModelContextProtocolMemoryMcpConnector : IMemoryMcpConnector
{
    public const string HttpClientName = "MemoryMcp";

    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public ModelContextProtocolMemoryMcpConnector(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public async Task<MemoryMcpConnection> DiscoverAsync(
        Uri endpoint,
        string token,
        CancellationToken cancellationToken)
    {
        await using var session = await OpenSessionAsync(endpoint, token, cancellationToken);

        return new MemoryMcpConnection(
            session.Client.ServerInfo?.Version,
            session.ToolCount);
    }

    public async Task<MemoryMcpToolResult> CallToolAsync(
        Uri endpoint,
        string token,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        await using var session = await OpenSessionAsync(endpoint, token, cancellationToken);

        var result = await session.Client.CallToolAsync(
            toolName,
            arguments,
            cancellationToken: cancellationToken);

        var text = string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        return new MemoryMcpToolResult(
            result.IsError == true,
            text,
            result.StructuredContent?.GetRawText());
    }

    private HttpClientTransportOptions CreateTransportOptions(Uri endpoint, string token) =>
        new()
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "Memory MCP",
            ConnectionTimeout = ConnectionTimeout,
            AdditionalHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {token.Trim()}",
            },
        };

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

    private async Task<SdkSession> OpenSessionAsync(
        Uri endpoint,
        string token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token is required.", nameof(token));
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        var transport = new HttpClientTransport(
            CreateTransportOptions(endpoint, token),
            httpClient,
            _loggerFactory,
            ownsHttpClient: false);

        try
        {
            var client = await McpClient.CreateAsync(
                transport,
                CreateClientOptions(),
                _loggerFactory,
                cancellationToken);

            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

                return new SdkSession(httpClient, transport, client, tools.Count);
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

    private sealed class SdkSession : IAsyncDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly HttpClientTransport _transport;

        public SdkSession(HttpClient httpClient, HttpClientTransport transport, McpClient client, int toolCount)
        {
            _httpClient = httpClient;
            _transport = transport;
            Client = client;
            ToolCount = toolCount;
        }

        public McpClient Client { get; }

        public int ToolCount { get; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _transport.DisposeAsync();
            _httpClient.Dispose();
        }
    }
}
