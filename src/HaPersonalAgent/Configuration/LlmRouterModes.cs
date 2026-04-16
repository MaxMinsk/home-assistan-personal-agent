namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: допустимые режимы adaptive LLM router.
/// Зачем: HAAG-048 требует запускать routing детерминированно и безопасно по фазам (off -> shadow -> enforced).
/// Как: Normalize приводит пользовательское значение к одному из стабильных режимов.
/// </summary>
public static class LlmRouterModes
{
    public const string Off = "off";
    public const string Shadow = "shadow";
    public const string Enforced = "enforced";

    public static bool IsValid(string? mode) =>
        string.Equals(Normalize(mode), mode?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? mode)
    {
        if (string.Equals(mode?.Trim(), Shadow, StringComparison.OrdinalIgnoreCase))
        {
            return Shadow;
        }

        if (string.Equals(mode?.Trim(), Enforced, StringComparison.OrdinalIgnoreCase))
        {
            return Enforced;
        }

        return Off;
    }
}
