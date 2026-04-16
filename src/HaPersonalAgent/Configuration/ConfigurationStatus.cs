namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: безопасный снимок текущей конфигурации без секретных значений.
/// Зачем: статус нужен в логах, status tool и будущей команде /status, но токены нельзя выводить даже случайно.
/// Как: From собирает только публичные поля и boolean-флаги configured/not configured из typed options.
/// </summary>
public sealed record ConfigurationStatus(
    string LlmProvider,
    string LlmBaseUrl,
    string LlmModel,
    string LlmThinkingMode,
    string LlmRouterMode,
    string LlmRouterSmallModel,
    int LlmRouterMaxInputCharsForSmall,
    int LlmRouterMaxHistoryMessagesForSmall,
    int LlmRouterSimpleMaxInputChars,
    int LlmRouterSimpleMaxHistoryMessages,
    bool LlmRouterSimpleAllowTools,
    string LlmRouterDeepKeywords,
    bool LlmApiKeyConfigured,
    bool TelegramBotTokenConfigured,
    int AllowedTelegramUserCount,
    bool TelegramReasoningPreviewEnabled,
    int TelegramReasoningPreviewDelaySeconds,
    string HomeAssistantUrl,
    string HomeAssistantMcpEndpoint,
    bool HomeAssistantTokenConfigured,
    string StateDatabasePath,
    string WorkspacePath,
    int WorkspaceMaxMb,
    string MemoryRetrievalMode,
    string CapsuleExtractionMode,
    int CapsuleAutoBatchRawEventThreshold)
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
            LlmThinkingModes.Normalize(llmOptions.ThinkingMode),
            LlmRouterModes.Normalize(llmOptions.RouterMode),
            string.IsNullOrWhiteSpace(llmOptions.RouterSmallModel)
                ? "moonshot-v1-8k"
                : llmOptions.RouterSmallModel.Trim(),
            Math.Clamp(llmOptions.RouterMaxInputCharsForSmall, 200, 24_000),
            Math.Clamp(llmOptions.RouterMaxHistoryMessagesForSmall, 2, 64),
            Math.Clamp(llmOptions.RouterSimpleMaxInputChars, 400, 24_000),
            Math.Clamp(llmOptions.RouterSimpleMaxHistoryMessages, 2, 64),
            llmOptions.RouterSimpleAllowTools,
            string.IsNullOrWhiteSpace(llmOptions.RouterDeepKeywords)
                ? "пошагово,step-by-step,deep reasoning"
                : llmOptions.RouterDeepKeywords.Trim(),
            !string.IsNullOrWhiteSpace(llmOptions.ApiKey),
            !string.IsNullOrWhiteSpace(telegramOptions.BotToken),
            telegramOptions.AllowedUserIds.Length,
            telegramOptions.ReasoningPreviewEnabled,
            Math.Clamp(telegramOptions.ReasoningPreviewDelaySeconds, 1, 30),
            homeAssistantOptions.Url,
            homeAssistantOptions.McpEndpoint,
            !string.IsNullOrWhiteSpace(homeAssistantOptions.LongLivedAccessToken),
            agentOptions.StateDatabasePath,
            agentOptions.WorkspacePath,
            agentOptions.WorkspaceMaxMb,
            AgentOptions.NormalizeMemoryRetrievalMode(agentOptions.MemoryRetrievalMode),
            string.IsNullOrWhiteSpace(agentOptions.CapsuleExtractionMode)
                ? AgentOptions.CapsuleExtractionModeManual
                : agentOptions.CapsuleExtractionMode.Trim(),
            Math.Max(agentOptions.CapsuleAutoBatchRawEventThreshold, 0));
    }
}
