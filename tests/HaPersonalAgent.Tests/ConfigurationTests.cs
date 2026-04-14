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
        var homeAssistantOptions = new HomeAssistantOptions();

        Assert.Equal("moonshot", llmOptions.Provider);
        Assert.Equal("https://api.moonshot.ai/v1", llmOptions.BaseUrl);
        Assert.Equal("kimi-k2.5", llmOptions.Model);
        Assert.Equal("/data/state.sqlite", agentOptions.StateDatabasePath);
        Assert.Equal("/data/workspace", agentOptions.WorkspacePath);
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
                  "ha_url": "http://homeassistant.local:8123",
                  "ha_long_lived_access_token": "ha-secret",
                  "mcp_endpoint": "/api/mcp",
                  "llm_provider": "moonshot",
                  "llm_base_url": "https://api.moonshot.ai/v1",
                  "llm_model": "kimi-k2.5",
                  "llm_api_key": "moonshot-secret",
                  "state_database_path": "/tmp/state.sqlite",
                  "workspace_path": "/tmp/workspace",
                  "workspace_max_mb": 128
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
            Assert.Equal("moonshot-secret", llmOptions.ApiKey);
            Assert.Equal("kimi-k2.5", llmOptions.Model);
            Assert.Equal("http://homeassistant.local:8123", homeAssistantOptions.Url);
            Assert.Equal("ha-secret", homeAssistantOptions.LongLivedAccessToken);
            Assert.Equal("/tmp/state.sqlite", agentOptions.StateDatabasePath);
            Assert.Equal("/tmp/workspace", agentOptions.WorkspacePath);
            Assert.Equal(128, agentOptions.WorkspaceMaxMb);
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
        Assert.True(status.TelegramBotTokenConfigured);
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
