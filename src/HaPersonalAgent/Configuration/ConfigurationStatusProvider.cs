using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: сервис для получения актуального безопасного статуса конфигурации.
/// Зачем: потребителям не нужно знать обо всех typed options и повторять маскирование секретов.
/// Как: через IOptions берет значения секций Agent, Telegram, Llm и HomeAssistant и собирает ConfigurationStatus.
/// </summary>
public sealed class ConfigurationStatusProvider
{
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly IOptions<HomeAssistantOptions> _homeAssistantOptions;
    private readonly IOptions<LlmOptions> _llmOptions;
    private readonly IOptions<MemoryMcpOptions> _memoryMcpOptions;
    private readonly IOptions<TelegramOptions> _telegramOptions;

    public ConfigurationStatusProvider(
        IOptions<AgentOptions> agentOptions,
        IOptions<TelegramOptions> telegramOptions,
        IOptions<LlmOptions> llmOptions,
        IOptions<HomeAssistantOptions> homeAssistantOptions,
        IOptions<MemoryMcpOptions> memoryMcpOptions)
    {
        _agentOptions = agentOptions;
        _telegramOptions = telegramOptions;
        _llmOptions = llmOptions;
        _homeAssistantOptions = homeAssistantOptions;
        _memoryMcpOptions = memoryMcpOptions;
    }

    public ConfigurationStatus Create() =>
        ConfigurationStatus.From(
            _agentOptions.Value,
            _telegramOptions.Value,
            _llmOptions.Value,
            _homeAssistantOptions.Value,
            _memoryMcpOptions.Value);
}
