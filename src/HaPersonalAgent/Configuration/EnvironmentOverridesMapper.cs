namespace HaPersonalAgent.Configuration;

public static class EnvironmentOverridesMapper
{
    private static readonly IReadOnlyDictionary<string, string> SecretAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MOONSHOT_API_KEY"] = $"{LlmOptions.SectionName}:ApiKey",
            ["TELEGRAM_BOT_TOKEN"] = $"{TelegramOptions.SectionName}:BotToken",
            ["HOME_ASSISTANT_LONG_LIVED_ACCESS_TOKEN"] = $"{HomeAssistantOptions.SectionName}:LongLivedAccessToken",
            ["HA_LONG_LIVED_ACCESS_TOKEN"] = $"{HomeAssistantOptions.SectionName}:LongLivedAccessToken",
        };

    public static IReadOnlyDictionary<string, string?> Map(IReadOnlyDictionary<string, string?> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);

        var mapped = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in SecretAliases)
        {
            if (environmentVariables.TryGetValue(alias.Key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                mapped[alias.Value] = value;
            }
        }

        return mapped;
    }
}
