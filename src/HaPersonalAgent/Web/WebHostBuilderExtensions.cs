using HaPersonalAgent.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace HaPersonalAgent.Web;

/// <summary>
/// Что: настройка Kestrel для встроенного веб-хоста до сборки приложения.
/// Зачем: Kestrel должен слушать ingress-порт только когда Web UI включён; при выключенном веб-слое нельзя открывать внешний порт.
/// Как: читает Web:Enabled/Web:Port из уже собранной конфигурации и задаёт URL (0.0.0.0:port при включённом, эфемерный loopback при выключенном) до Build().
/// </summary>
public static class WebHostBuilderExtensions
{
    public static WebApplicationBuilder ConfigureAgentWebHost(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var enabled = builder.Configuration.GetValue<bool?>($"{WebHostOptions.SectionName}:Enabled") ?? true;
        var port = builder.Configuration.GetValue<int?>($"{WebHostOptions.SectionName}:Port") ?? WebHostOptions.DefaultPort;

        // Включён: слушаем ingress-порт на всех интерфейсах (HA проксирует его через Ingress).
        // Выключен: биндим эфемерный loopback-порт, чтобы процесс жил, но внешнего веб-доступа не было.
        builder.WebHost.UseUrls(enabled
            ? $"http://0.0.0.0:{port}"
            : "http://127.0.0.1:0");

        return builder;
    }
}
