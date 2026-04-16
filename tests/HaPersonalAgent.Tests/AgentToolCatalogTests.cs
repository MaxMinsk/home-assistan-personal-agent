using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.HomeAssistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: unit-тесты tool catalog после декомпозиции AgentRuntime.
/// Зачем: гарантирует, что rules включения tools/instructions проверяются отдельно от orchestration.
/// Как: валидирует no-tools и tool-enabled сценарии на минимальном наборе зависимостей.
/// </summary>
public class AgentToolCatalogTests
{
    [Fact]
    public void CreateTools_returns_empty_for_no_tools_profile()
    {
        var catalog = new AgentToolCatalog(
            new AgentStatusTool(CreateStatusProvider()),
            NullLogger<AgentToolCatalog>.Instance);
        var executionPlan = CreatePlan(LlmExecutionProfile.PureChat);
        var context = AgentContext.Create();
        var toolSet = HomeAssistantMcpAgentToolSet.Unavailable(
            HomeAssistantMcpStatus.NotConfigured,
            "disabled");

        var tools = catalog.CreateTools(
            context,
            executionPlan,
            toolSet);

        Assert.Empty(tools);
    }

    [Fact]
    public void CreateTools_includes_status_for_tool_enabled_profile()
    {
        var catalog = new AgentToolCatalog(
            new AgentStatusTool(CreateStatusProvider()),
            NullLogger<AgentToolCatalog>.Instance);
        var executionPlan = CreatePlan(LlmExecutionProfile.ToolEnabled);
        var context = AgentContext.Create();
        var toolSet = HomeAssistantMcpAgentToolSet.Available(
            Array.Empty<Microsoft.Extensions.AI.AIFunction>(),
            Array.Empty<HomeAssistantMcpItemInfo>(),
            totalToolCount: 0,
            ownsSession: null);

        var tools = catalog.CreateTools(
            context,
            executionPlan,
            toolSet);

        Assert.Single(tools);
        Assert.Equal("status", tools[0].Name);
    }

    [Fact]
    public void CreateInstructions_contains_no_tools_guidance_for_pure_chat()
    {
        var catalog = new AgentToolCatalog(
            new AgentStatusTool(CreateStatusProvider()),
            NullLogger<AgentToolCatalog>.Instance);
        var executionPlan = CreatePlan(LlmExecutionProfile.PureChat);
        var context = AgentContext.Create();
        var toolSet = HomeAssistantMcpAgentToolSet.Unavailable(
            HomeAssistantMcpStatus.NotConfigured,
            "disabled");

        var instructions = catalog.CreateInstructions(
            context,
            executionPlan,
            toolSet);

        Assert.Contains("no-tools profile", instructions, StringComparison.OrdinalIgnoreCase);
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
            LlmThinkingModes.Auto,
            LlmEffectiveThinkingMode.ProviderDefault,
            "test");

    private static ConfigurationStatusProvider CreateStatusProvider() =>
        new(
            Microsoft.Extensions.Options.Options.Create(new AgentOptions()),
            Microsoft.Extensions.Options.Options.Create(new TelegramOptions()),
            Microsoft.Extensions.Options.Options.Create(new LlmOptions { ApiKey = "configured" }),
            Microsoft.Extensions.Options.Options.Create(new HomeAssistantOptions()));
}
