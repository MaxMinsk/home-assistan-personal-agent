namespace HaPersonalAgent;

/// <summary>
/// Что: централизованные сведения о приложении, которые нужны логам, статусу и тестам.
/// Зачем: держим имя, версию и target framework в одном месте, чтобы не размазывать константы по проекту.
/// Как: статические свойства читаются без DI; версия берется из assembly metadata, а учебный target framework задан явно.
/// </summary>
public static class ApplicationInfo
{
    public const string Name = "Home Assistant Personal Agent";

    public const string TargetFramework = "net8.0";

    public static string Version =>
        typeof(ApplicationInfo).Assembly.GetName().Version?.ToString() ?? "unknown";
}
