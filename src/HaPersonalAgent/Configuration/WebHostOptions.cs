namespace HaPersonalAgent.Configuration;

/// <summary>
/// Что: настройки встроенного веб-хоста add-on (Web UI + JSON API за Home Assistant Ingress).
/// Зачем: worker-процессу нужно знать, поднимать ли Kestrel, на каком порту слушать и каким токеном закрывать прямой (не через Ingress) доступ.
/// Как: Enabled включает веб-слой; Port совпадает с ingress_port из addon/config.yaml; ApiToken опционально защищает проброшенный порт; секция биндится из HA options так же, как остальные *Options.
/// </summary>
public sealed class WebHostOptions
{
    public const string SectionName = "Web";

    /// <summary>Порт, который слушает Kestrel; ДОЛЖЕН совпадать с ingress_port в addon/config.yaml.</summary>
    public const int DefaultPort = 8099;

    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = DefaultPort;

    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Задан ли api-token для защиты прямого (не через HA Ingress) доступа к веб-хосту.</summary>
    public bool IsApiTokenConfigured => !string.IsNullOrWhiteSpace(ApiToken);
}
