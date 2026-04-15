namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: строковые значения настройки reasoning/thinking режима LLM.
/// Зачем: add-on UI и env/config должны использовать стабильные lowercase значения без привязки к enum binding.
/// Как: Normalize приводит ввод к supported value, а IsValid используется health-check логикой runtime.
/// </summary>
public static class LlmThinkingModes
{
    public const string Auto = "auto";
    public const string Disabled = "disabled";
    public const string Enabled = "enabled";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Auto;
        }

        return value.Trim().ToLowerInvariant();
    }

    public static bool IsValid(string? value)
    {
        var normalized = Normalize(value);

        return normalized is Auto or Disabled or Enabled;
    }
}
