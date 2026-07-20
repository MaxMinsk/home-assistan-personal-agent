namespace HaPersonalAgent.Search;

/// <summary>
/// Что: одна находка веб-поиска.
/// Зачем: агент обязан ссылаться на источник, а не пересказывать по памяти — поэтому URL здесь обязателен.
/// Как: заголовок, адрес и краткое описание (сниппет) от провайдера; полный текст страницы Brave не отдаёт.
/// </summary>
public sealed record WebSearchResult(
    string Title,
    string Url,
    string Description,
    string? Age);

/// <summary>
/// Что: результат одного веб-поиска целиком.
/// Зачем: инструменту нужно уметь честно сказать «поиск недоступен» или «ничего не нашлось», а не молча вернуть пустоту.
/// Как: IsAvailable отделяет «не настроено/ошибка» от «настроено, но результатов нет»; Reason объясняет причину.
/// </summary>
public sealed record WebSearchResponse(
    bool IsAvailable,
    string Query,
    IReadOnlyList<WebSearchResult> Results,
    string? Reason)
{
    public static WebSearchResponse Unavailable(string query, string reason) =>
        new(false, query, Array.Empty<WebSearchResult>(), reason);

    public static WebSearchResponse Found(string query, IReadOnlyList<WebSearchResult> results) =>
        new(true, query, results, null);
}

/// <summary>
/// Что: контракт провайдера веб-поиска.
/// Зачем: провайдера меняют (Brave сегодня, Tavily/SearXNG завтра) — остальной код не должен об этом знать.
/// Как: реализация сама решает, настроена ли она; каталог инструментов регистрирует web_search только когда IsConfigured.
/// </summary>
public interface IWebSearchProvider
{
    bool IsConfigured { get; }

    Task<WebSearchResponse> SearchAsync(string query, int count, CancellationToken cancellationToken);
}
