using System.Net;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HaPersonalAgent.Tests;

public sealed class MemoryMcpTests
{
    [Theory]
    [InlineData("https://memory.kazmin.tech/mcp", true)]
    [InlineData("http://homeassistant.local:8099/mcp", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("/api/mcp", false)]
    [InlineData("ftp://example.com/mcp", false)]
    [InlineData("not a url", false)]
    public void EndpointBuilder_validates_absolute_http_endpoints(string endpoint, bool expected)
    {
        var ok = MemoryMcpEndpointBuilder.TryBuild(endpoint, out var uri, out var reason);

        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.NotNull(uri);
        }
        else
        {
            Assert.Null(uri);
            Assert.NotNull(reason);
        }
    }

    [Fact]
    public async Task DiscoverAsync_returns_NotConfigured_when_endpoint_empty()
    {
        var client = CreateClient(new MemoryMcpOptions(), new FakeConnector());

        var result = await client.DiscoverAsync(CancellationToken.None);

        Assert.Equal(MemoryMcpStatus.NotConfigured, result.Status);
    }

    [Fact]
    public async Task DiscoverAsync_returns_NotConfigured_when_token_empty()
    {
        var options = new MemoryMcpOptions { Endpoint = "https://memory.kazmin.tech/mcp" };
        var client = CreateClient(options, new FakeConnector());

        var result = await client.DiscoverAsync(CancellationToken.None);

        Assert.Equal(MemoryMcpStatus.NotConfigured, result.Status);
        Assert.False(result.TokenConfigured);
    }

    [Fact]
    public async Task DiscoverAsync_returns_InvalidConfiguration_for_non_http_endpoint()
    {
        var options = new MemoryMcpOptions { Endpoint = "ftp://example.com/mcp", Token = "t" };
        var client = CreateClient(options, new FakeConnector());

        var result = await client.DiscoverAsync(CancellationToken.None);

        Assert.Equal(MemoryMcpStatus.InvalidConfiguration, result.Status);
    }

    [Fact]
    public async Task DiscoverAsync_returns_Reachable_on_success()
    {
        var options = new MemoryMcpOptions { Endpoint = "https://memory.kazmin.tech/mcp", Token = "t" };
        var connector = new FakeConnector { Connection = new MemoryMcpConnection("0.43.0", 30) };
        var client = CreateClient(options, connector);

        var result = await client.DiscoverAsync(CancellationToken.None);

        Assert.Equal(MemoryMcpStatus.Reachable, result.Status);
        Assert.Equal("0.43.0", result.ServerVersion);
        Assert.Equal(30, result.ToolCount);
    }

    [Fact]
    public async Task DiscoverAsync_maps_unauthorized_to_AuthFailed()
    {
        var options = new MemoryMcpOptions { Endpoint = "https://memory.kazmin.tech/mcp", Token = "t" };
        var connector = new FakeConnector
        {
            DiscoverError = new HttpRequestException("unauthorized", inner: null, HttpStatusCode.Unauthorized),
        };
        var client = CreateClient(options, connector);

        var result = await client.DiscoverAsync(CancellationToken.None);

        Assert.Equal(MemoryMcpStatus.AuthFailed, result.Status);
    }

    [Fact]
    public async Task CallToolAsync_throws_when_not_configured()
    {
        var client = CreateClient(new MemoryMcpOptions(), new FakeConnector());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallToolAsync("status", arguments: null, CancellationToken.None));
    }

    [Fact]
    public async Task CallToolAsync_delegates_to_connector()
    {
        var options = new MemoryMcpOptions { Endpoint = "https://memory.kazmin.tech/mcp", Token = "t" };
        var connector = new FakeConnector { ToolResult = new MemoryMcpToolResult(false, "ok", null) };
        var client = CreateClient(options, connector);

        var result = await client.CallToolAsync("status", arguments: null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ok", result.Text);
        Assert.Equal("status", connector.LastToolName);
    }

    [Fact]
    public void ConfigurationStatus_includes_memory_mcp_fields_without_token()
    {
        var status = ConfigurationStatus.From(
            new AgentOptions(),
            new TelegramOptions(),
            new LlmOptions(),
            new HomeAssistantOptions(),
            new MemoryMcpOptions
            {
                Endpoint = "https://memory.kazmin.tech/mcp",
                Token = "secret-token",
                Domain = "development",
                Project = "ha-personal-agent",
                StoreType = MemoryMcpOptions.StoreTypeMemoryMcp,
            });

        Assert.True(status.MemoryMcpEndpointConfigured);
        Assert.True(status.MemoryMcpTokenConfigured);
        Assert.Equal("development", status.MemoryMcpDomain);
        Assert.Equal("ha-personal-agent", status.MemoryMcpProject);
        Assert.Equal(MemoryMcpOptions.StoreTypeMemoryMcp, status.MemoryStoreType);
        Assert.DoesNotContain("secret-token", status.ToString(), StringComparison.Ordinal);
    }

    private static MemoryMcpClient CreateClient(MemoryMcpOptions options, IMemoryMcpConnector connector) =>
        new(Options.Create(options), connector, NullLogger<MemoryMcpClient>.Instance);

    private sealed class FakeConnector : IMemoryMcpConnector
    {
        public MemoryMcpConnection Connection { get; set; } = new("0.0.0", 0);

        public MemoryMcpToolResult ToolResult { get; set; } = new(false, string.Empty, null);

        public Exception? DiscoverError { get; set; }

        public string? LastToolName { get; private set; }

        public Task<MemoryMcpConnection> DiscoverAsync(Uri endpoint, string token, CancellationToken cancellationToken) =>
            DiscoverError is not null
                ? Task.FromException<MemoryMcpConnection>(DiscoverError)
                : Task.FromResult(Connection);

        public Task<MemoryMcpToolResult> CallToolAsync(
            Uri endpoint,
            string token,
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            LastToolName = toolName;
            return Task.FromResult(ToolResult);
        }
    }
}
