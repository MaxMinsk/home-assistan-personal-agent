using System.Collections;
using Microsoft.Extensions.Configuration;

namespace HaPersonalAgent.Configuration;

public static class ConfigurationBuilderExtensions
{
    public const string DefaultHomeAssistantAddOnOptionsPath = "/data/options.json";

    public static IConfigurationBuilder AddHomeAssistantAddOnOptions(
        this IConfigurationBuilder builder,
        string path = DefaultHomeAssistantAddOnOptionsPath)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var mappedOptions = HomeAssistantAddOnOptionsMapper.MapFileIfExists(path);
        if (mappedOptions.Count > 0)
        {
            builder.AddInMemoryCollection(mappedOptions);
        }

        return builder;
    }

    public static IConfigurationBuilder AddAgentEnvironmentOverrides(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var environmentVariables = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(
                entry => entry.Key.ToString() ?? string.Empty,
                entry => entry.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase);

        var mappedOptions = EnvironmentOverridesMapper.Map(environmentVariables);
        if (mappedOptions.Count > 0)
        {
            builder.AddInMemoryCollection(mappedOptions);
        }

        return builder;
    }
}
