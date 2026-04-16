using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Configuration;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты configuration слоя.
/// Зачем: Home Assistant add-on options, env aliases и маскирование секретов являются критичным runtime-контрактом.
/// Как: собирает IConfiguration из in-memory/temp источников и проверяет binding в typed options без реальных секретов.
/// </summary>
public class ConfigurationTests
{
    [Fact]
    public void Options_defaults_use_moonshot_and_home_assistant_paths()
    {
        var llmOptions = new LlmOptions();
        var agentOptions = new AgentOptions();
        var telegramOptions = new TelegramOptions();
        var homeAssistantOptions = new HomeAssistantOptions();

        Assert.Equal("moonshot", llmOptions.Provider);
        Assert.Equal("https://api.moonshot.ai/v1", llmOptions.BaseUrl);
        Assert.Equal("kimi-k2.5", llmOptions.Model);
        Assert.Equal(LlmThinkingModes.Auto, llmOptions.ThinkingMode);
        Assert.Equal(LlmRouterModes.Off, llmOptions.RouterMode);
        Assert.Equal("moonshot-v1-8k", llmOptions.RouterSmallModel);
        Assert.Equal(1800, llmOptions.RouterMaxInputCharsForSmall);
        Assert.Equal(10, llmOptions.RouterMaxHistoryMessagesForSmall);
        Assert.Equal(6000, llmOptions.RouterSimpleMaxInputChars);
        Assert.Equal(6, llmOptions.RouterSimpleMaxHistoryMessages);
        Assert.False(llmOptions.RouterSimpleAllowTools);
        Assert.Equal("/data/state.sqlite", agentOptions.StateDatabasePath);
        Assert.Equal("/data/workspace", agentOptions.WorkspacePath);
        Assert.Equal(AgentOptions.MemoryRetrievalModeBeforeInvoke, agentOptions.MemoryRetrievalMode);
        Assert.Equal(AgentOptions.CapsuleExtractionModeManual, agentOptions.CapsuleExtractionMode);
        Assert.Equal(20, agentOptions.CapsuleAutoBatchRawEventThreshold);
        Assert.False(telegramOptions.ReasoningPreviewEnabled);
        Assert.Equal(7, telegramOptions.ReasoningPreviewDelaySeconds);
        Assert.Equal("http://supervisor/core", homeAssistantOptions.Url);
        Assert.Equal("/api/mcp", homeAssistantOptions.McpEndpoint);
    }

    [Fact]
    public void Addon_options_file_is_optional()
    {
        var missingOptionsPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        var configuration = new ConfigurationBuilder()
            .AddHomeAssistantAddOnOptions(missingOptionsPath)
            .Build();

        var llmOptions = Bind<LlmOptions>(configuration, LlmOptions.SectionName);

        Assert.Equal("moonshot", llmOptions.Provider);
        Assert.Equal("https://api.moonshot.ai/v1", llmOptions.BaseUrl);
    }

    [Fact]
    public void Addon_options_mapper_binds_snake_case_home_assistant_options()
    {
        var optionsPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(
                optionsPath,
                """
                {
                  "telegram_bot_token": "telegram-secret",
                  "allowed_telegram_user_ids": [123, "456"],
                  "reasoning_preview_enabled": true,
                  "reasoning_preview_delay_seconds": 9,
                  "ha_url": "http://homeassistant.local:8123",
                  "ha_long_lived_access_token": "ha-secret",
                  "mcp_endpoint": "/api/mcp",
                  "llm_provider": "moonshot",
                  "llm_base_url": "https://api.moonshot.ai/v1",
                  "llm_model": "kimi-k2.5",
                  "llm_api_key": "moonshot-secret",
                  "llm_thinking_mode": "disabled",
                  "llm_router_mode": "enforced",
                  "llm_router_small_model": "moonshot-v1-8k",
                  "llm_router_max_input_chars_for_small": 1600,
                  "llm_router_max_history_messages_for_small": 8,
                  "llm_router_simple_max_input_chars": 4200,
                  "llm_router_simple_max_history_messages": 5,
                  "llm_router_simple_allow_tools": true,
                  "llm_router_deep_keywords": "пошагово,deep reasoning",
                  "state_database_path": "/tmp/state.sqlite",
                  "workspace_path": "/tmp/workspace",
                  "workspace_max_mb": 128,
                  "memory_retrieval_mode": "on_demand_tool",
                  "capsule_extraction_mode": "auto-batched",
                  "capsule_auto_batch_raw_event_threshold": 30
                }
                """);

            var configuration = new ConfigurationBuilder()
                .AddHomeAssistantAddOnOptions(optionsPath)
                .Build();

            var telegramOptions = Bind<TelegramOptions>(configuration, TelegramOptions.SectionName);
            var llmOptions = Bind<LlmOptions>(configuration, LlmOptions.SectionName);
            var homeAssistantOptions = Bind<HomeAssistantOptions>(configuration, HomeAssistantOptions.SectionName);
            var agentOptions = Bind<AgentOptions>(configuration, AgentOptions.SectionName);

            Assert.Equal("telegram-secret", telegramOptions.BotToken);
            Assert.Equal(new long[] { 123, 456 }, telegramOptions.AllowedUserIds);
            Assert.True(telegramOptions.ReasoningPreviewEnabled);
            Assert.Equal(9, telegramOptions.ReasoningPreviewDelaySeconds);
            Assert.Equal("moonshot-secret", llmOptions.ApiKey);
            Assert.Equal("kimi-k2.5", llmOptions.Model);
            Assert.Equal(LlmThinkingModes.Disabled, llmOptions.ThinkingMode);
            Assert.Equal(LlmRouterModes.Enforced, llmOptions.RouterMode);
            Assert.Equal("moonshot-v1-8k", llmOptions.RouterSmallModel);
            Assert.Equal(1600, llmOptions.RouterMaxInputCharsForSmall);
            Assert.Equal(8, llmOptions.RouterMaxHistoryMessagesForSmall);
            Assert.Equal(4200, llmOptions.RouterSimpleMaxInputChars);
            Assert.Equal(5, llmOptions.RouterSimpleMaxHistoryMessages);
            Assert.True(llmOptions.RouterSimpleAllowTools);
            Assert.Equal("пошагово,deep reasoning", llmOptions.RouterDeepKeywords);
            Assert.Equal("http://homeassistant.local:8123", homeAssistantOptions.Url);
            Assert.Equal("ha-secret", homeAssistantOptions.LongLivedAccessToken);
            Assert.Equal("/tmp/state.sqlite", agentOptions.StateDatabasePath);
            Assert.Equal("/tmp/workspace", agentOptions.WorkspacePath);
            Assert.Equal(128, agentOptions.WorkspaceMaxMb);
            Assert.Equal(AgentOptions.MemoryRetrievalModeOnDemandTool, agentOptions.MemoryRetrievalMode);
            Assert.Equal("auto-batched", agentOptions.CapsuleExtractionMode);
            Assert.Equal(30, agentOptions.CapsuleAutoBatchRawEventThreshold);
        }
        finally
        {
            File.Delete(optionsPath);
        }
    }

    [Theory]
    [InlineData("""{"allowed_telegram_user_ids": "123, 456 789"}""", new long[] { 123, 456, 789 })]
    [InlineData("""{"allowed_telegram_user_ids": 93372553}""", new long[] { 93372553 })]
    [InlineData("""{"allowed_telegram_user_ids": ""}""", new long[] { })]
    public void Addon_options_mapper_accepts_ui_friendly_telegram_user_ids(
        string json,
        long[] expectedUserIds)
    {
        var mapped = HomeAssistantAddOnOptionsMapper.MapJson(json);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(mapped)
            .Build();

        var telegramOptions = Bind<TelegramOptions>(configuration, TelegramOptions.SectionName);

        Assert.Equal(expectedUserIds, telegramOptions.AllowedUserIds);
    }

    [Fact]
    public void Environment_aliases_override_secret_values()
    {
        var environmentVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MOONSHOT_API_KEY"] = "moonshot-secret",
            ["TELEGRAM_BOT_TOKEN"] = "telegram-secret",
            ["HOME_ASSISTANT_LONG_LIVED_ACCESS_TOKEN"] = "ha-secret",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:ApiKey"] = "old-llm-secret",
                ["Telegram:BotToken"] = "old-telegram-secret",
                ["HomeAssistant:LongLivedAccessToken"] = "old-ha-secret",
            })
            .AddInMemoryCollection(EnvironmentOverridesMapper.Map(environmentVariables))
            .Build();

        var telegramOptions = Bind<TelegramOptions>(configuration, TelegramOptions.SectionName);
        var llmOptions = Bind<LlmOptions>(configuration, LlmOptions.SectionName);
        var homeAssistantOptions = Bind<HomeAssistantOptions>(configuration, HomeAssistantOptions.SectionName);

        Assert.Equal("moonshot-secret", llmOptions.ApiKey);
        Assert.Equal("telegram-secret", telegramOptions.BotToken);
        Assert.Equal("ha-secret", homeAssistantOptions.LongLivedAccessToken);
    }

    [Fact]
    public void Configuration_status_does_not_include_raw_secret_values()
    {
        var status = ConfigurationStatus.From(
            new AgentOptions(),
            new TelegramOptions
            {
                BotToken = "telegram-secret",
                AllowedUserIds = new long[] { 123 },
            },
            new LlmOptions
            {
                ApiKey = "moonshot-secret",
            },
            new HomeAssistantOptions
            {
                LongLivedAccessToken = "ha-secret",
            });

        var statusText = status.ToString();

        Assert.True(status.LlmApiKeyConfigured);
        Assert.Equal(LlmThinkingModes.Auto, status.LlmThinkingMode);
        Assert.Equal(LlmRouterModes.Off, status.LlmRouterMode);
        Assert.Equal("moonshot-v1-8k", status.LlmRouterSmallModel);
        Assert.Equal(1800, status.LlmRouterMaxInputCharsForSmall);
        Assert.Equal(10, status.LlmRouterMaxHistoryMessagesForSmall);
        Assert.Equal(6000, status.LlmRouterSimpleMaxInputChars);
        Assert.Equal(6, status.LlmRouterSimpleMaxHistoryMessages);
        Assert.False(status.LlmRouterSimpleAllowTools);
        Assert.Equal(AgentOptions.MemoryRetrievalModeBeforeInvoke, status.MemoryRetrievalMode);
        Assert.True(status.TelegramBotTokenConfigured);
        Assert.False(status.TelegramReasoningPreviewEnabled);
        Assert.Equal(7, status.TelegramReasoningPreviewDelaySeconds);
        Assert.True(status.HomeAssistantTokenConfigured);
        Assert.DoesNotContain("moonshot-secret", statusText);
        Assert.DoesNotContain("telegram-secret", statusText);
        Assert.DoesNotContain("ha-secret", statusText);
    }

    private static TOptions Bind<TOptions>(IConfiguration configuration, string sectionName)
        where TOptions : new()
    {
        var options = new TOptions();
        configuration.GetSection(sectionName).Bind(options);
        return options;
    }
}
