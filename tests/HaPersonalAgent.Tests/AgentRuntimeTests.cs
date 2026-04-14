using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты первого MAF runtime spike без реального сетевого вызова к LLM.
/// Зачем: важно проверить поведение без API key и безопасность status tool до подключения Telegram.
/// Как: создает runtime на Options.Create и пустом ServiceProvider, а status проверяет на отсутствие сырых секретов.
/// </summary>
public class AgentRuntimeTests
{
    [Fact]
    public async Task Runtime_reports_not_configured_without_llm_api_key()
    {
        var runtime = CreateRuntime(new LlmOptions
        {
            ApiKey = "",
        });

        var health = runtime.GetHealth();
        var response = await runtime.SendAsync(
            "hello",
            AgentContext.Create("test-correlation"),
            CancellationToken.None);

        Assert.False(health.IsConfigured);
        Assert.False(response.IsConfigured);
        Assert.Equal("test-correlation", response.CorrelationId);
        Assert.Contains("not configured", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_tool_returns_safe_status_without_raw_secrets()
    {
        var statusProvider = CreateConfigurationStatusProvider(
            new LlmOptions
            {
                ApiKey = "moonshot-secret",
            },
            new TelegramOptions
            {
                BotToken = "telegram-secret",
                AllowedUserIds = new long[] { 123 },
            },
            new HomeAssistantOptions
            {
                LongLivedAccessToken = "ha-secret",
            },
            new AgentOptions());
        var tool = new AgentStatusTool(statusProvider);

        var status = tool.GetStatus();
        var statusText = status.ToString();

        Assert.Equal(ApplicationInfo.Name, status.ApplicationName);
        Assert.Equal(ApplicationInfo.TargetFramework, status.TargetFramework);
        Assert.Equal("local", status.ConfigurationMode);
        Assert.DoesNotContain("moonshot-secret", statusText);
        Assert.DoesNotContain("telegram-secret", statusText);
        Assert.DoesNotContain("ha-secret", statusText);
    }

    private static AgentRuntime CreateRuntime(LlmOptions llmOptions)
    {
        var statusProvider = CreateConfigurationStatusProvider(
            llmOptions,
            new TelegramOptions(),
            new HomeAssistantOptions(),
            new AgentOptions());

        return new AgentRuntime(
            Options.Create(llmOptions),
            new AgentStatusTool(statusProvider),
            LoggerFactory.Create(_ => { }),
            serviceProvider: new EmptyServiceProvider());
    }

    private static ConfigurationStatusProvider CreateConfigurationStatusProvider(
        LlmOptions llmOptions,
        TelegramOptions telegramOptions,
        HomeAssistantOptions homeAssistantOptions,
        AgentOptions agentOptions) =>
        new(
            Options.Create(agentOptions),
            Options.Create(telegramOptions),
            Options.Create(llmOptions),
            Options.Create(homeAssistantOptions));

    /// <summary>
    /// Что: минимальная заглушка IServiceProvider для unit-тестов runtime.
    /// Зачем: AgentRuntime ожидает service provider для MAF tools, но эти тесты не должны поднимать настоящий DI container.
    /// Как: всегда возвращает null, потому что проверяем сценарии, где tool dependencies уже переданы напрямую.
    /// </summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
