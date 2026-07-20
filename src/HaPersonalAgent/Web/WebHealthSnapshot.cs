namespace HaPersonalAgent.Web;

/// <summary>
/// Что: безопасный снимок состояния веб-хоста для эндпоинта <c>/api/health</c>.
/// Зачем: HA Ingress и внешние liveness-проверки должны видеть, что процесс жив и какая версия запущена, без раскрытия секретов.
/// Как: собирает имя/версию/target framework из <see cref="ApplicationInfo"/> плюс флаг включённости Web UI; намеренно не содержит токенов и ключей.
/// </summary>
public sealed record WebHealthSnapshot(
    string Status,
    string Application,
    string Version,
    string TargetFramework,
    bool WebUiEnabled)
{
    public static WebHealthSnapshot Create(bool webUiEnabled) =>
        new(
            Status: "ok",
            Application: ApplicationInfo.Name,
            Version: ApplicationInfo.Version,
            TargetFramework: ApplicationInfo.TargetFramework,
            WebUiEnabled: webUiEnabled);
}
