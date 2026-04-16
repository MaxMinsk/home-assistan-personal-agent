using HaPersonalAgent.Agent;
using HaPersonalAgent.HomeAssistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: unit-тесты resolver-а MCP tool set.
/// Зачем: проверяет изоляцию логики graceful fallback при выключенных tools и отсутствии провайдера.
/// Как: использует fake provider и валидирует статус возвращаемого HomeAssistantMcpAgentToolSet.
/// </summary>
public class HomeAssistantMcpToolSetResolverTests
{
    [Fact]
    public async Task CreateAsync_returns_unavailable_when_profile_disables_tools()
    {
        var resolver = new HomeAssistantMcpToolSetResolver(
            homeAssistantMcpToolProvider: null,
            NullLogger<HomeAssistantMcpToolSetResolver>.Instance);
        var plan = CreatePlan(LlmExecutionProfile.PureChat);

        var set = await resolver.CreateAsync(plan, CancellationToken.None);

        Assert.Equal(HomeAssistantMcpStatus.NotConfigured, set.Status);
        Assert.Empty(set.Tools);
    }

    [Fact]
    public async Task CreateAsync_uses_provider_when_tools_enabled()
    {
        var expectedSet = HomeAssistantMcpAgentToolSet.Available(
            Array.Empty<Microsoft.Extensions.AI.AIFunction>(),
            Array.Empty<HomeAssistantMcpItemInfo>(),
            totalToolCount: 0,
            ownsSession: null);
        var resolver = new HomeAssistantMcpToolSetResolver(
            new FakeProvider(expectedSet),
            NullLogger<HomeAssistantMcpToolSetResolver>.Instance);
        var plan = CreatePlan(LlmExecutionProfile.ToolEnabled);

        var set = await resolver.CreateAsync(plan, CancellationToken.None);

        Assert.Equal(HomeAssistantMcpStatus.Reachable, set.Status);
        Assert.Same(expectedSet, set);
    }

    private static LlmExecutionPlan CreatePlan(LlmExecutionProfile profile) =>
        new(
            profile,
            new LlmProviderCapabilities(
                ProviderKey: "test-provider",
                SupportsTools: true,
                SupportsStreaming: true,
                SupportsReasoning: false,
                RequiresReasoningContentRoundTripForToolCalls: false,
                SupportsReasoningContentRoundTrip: false,
                SupportsExplicitThinkingEnable: false,
                ThinkingControlStyle: LlmThinkingControlStyle.None),
            "auto",
            LlmEffectiveThinkingMode.ProviderDefault,
            "test");

    private sealed class FakeProvider : IHomeAssistantMcpAgentToolProvider
    {
        private readonly HomeAssistantMcpAgentToolSet _toolSet;

        public FakeProvider(HomeAssistantMcpAgentToolSet toolSet)
        {
            _toolSet = toolSet;
        }

        public Task<HomeAssistantMcpAgentToolSet> CreateReadOnlyToolSetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_toolSet);
    }
}
