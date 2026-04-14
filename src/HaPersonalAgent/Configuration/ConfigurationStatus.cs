namespace HaPersonalAgent.Configuration;

public sealed record ConfigurationStatus(
    string LlmProvider,
    string LlmBaseUrl,
    string LlmModel,
    bool LlmApiKeyConfigured,
    bool TelegramBotTokenConfigured,
    int AllowedTelegramUserCount,
    string HomeAssistantUrl,
    string HomeAssistantMcpEndpoint,
    bool HomeAssistantTokenConfigured,
    string StateDatabasePath,
    string WorkspacePath,
    int WorkspaceMaxMb)
{
    public static ConfigurationStatus From(
        AgentOptions agentOptions,
        TelegramOptions telegramOptions,
        LlmOptions llmOptions,
        HomeAssistantOptions homeAssistantOptions)
    {
        ArgumentNullException.ThrowIfNull(agentOptions);
        ArgumentNullException.ThrowIfNull(telegramOptions);
        ArgumentNullException.ThrowIfNull(llmOptions);
        ArgumentNullException.ThrowIfNull(homeAssistantOptions);

        return new ConfigurationStatus(
            llmOptions.Provider,
            llmOptions.BaseUrl,
            llmOptions.Model,
            !string.IsNullOrWhiteSpace(llmOptions.ApiKey),
            !string.IsNullOrWhiteSpace(telegramOptions.BotToken),
            telegramOptions.AllowedUserIds.Length,
            homeAssistantOptions.Url,
            homeAssistantOptions.McpEndpoint,
            !string.IsNullOrWhiteSpace(homeAssistantOptions.LongLivedAccessToken),
            agentOptions.StateDatabasePath,
            agentOptions.WorkspacePath,
            agentOptions.WorkspaceMaxMb);
    }
}
