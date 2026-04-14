using HaPersonalAgent;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты для статических metadata приложения.
/// Зачем: имя и target framework используются в логах/status и должны меняться осознанно.
/// Как: проверяет ожидаемые значения ApplicationInfo без запуска host и внешних интеграций.
/// </summary>
public class ApplicationInfoTests
{
    [Fact]
    public void ApplicationInfo_has_expected_skeleton_metadata()
    {
        Assert.Equal("Home Assistant Personal Agent", ApplicationInfo.Name);
        Assert.Equal("net8.0", ApplicationInfo.TargetFramework);
    }
}
