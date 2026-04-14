using System.Collections;
using Microsoft.Extensions.Configuration;

namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: расширения для сборки конфигурации приложения.
/// Зачем: Home Assistant add-on хранит UI options в /data/options.json, а локальный запуск удобнее дополнять env aliases.
/// Как: методы читают внешние источники, преобразуют их в обычные .NET configuration keys и добавляют как in-memory provider.
/// </summary>
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
