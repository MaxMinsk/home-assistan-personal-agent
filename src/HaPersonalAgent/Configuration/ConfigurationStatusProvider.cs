using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Configuration;

public sealed class ConfigurationStatusProvider
{
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly IOptions<HomeAssistantOptions> _homeAssistantOptions;
    private readonly IOptions<LlmOptions> _llmOptions;
    private readonly IOptions<TelegramOptions> _telegramOptions;

    public ConfigurationStatusProvider(
        IOptions<AgentOptions> agentOptions,
        IOptions<TelegramOptions> telegramOptions,
        IOptions<LlmOptions> llmOptions,
        IOptions<HomeAssistantOptions> homeAssistantOptions)
    {
        _agentOptions = agentOptions;
        _telegramOptions = telegramOptions;
        _llmOptions = llmOptions;
        _homeAssistantOptions = homeAssistantOptions;
    }

    public ConfigurationStatus Create() =>
        ConfigurationStatus.From(
            _agentOptions.Value,
            _telegramOptions.Value,
            _llmOptions.Value,
            _homeAssistantOptions.Value);
}
