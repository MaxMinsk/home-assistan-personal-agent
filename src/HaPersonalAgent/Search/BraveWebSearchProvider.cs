using System.Globalization;
using System.Text.Json;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Search;

/// <summary>
/// Что: провайдер веб-поиска поверх Brave Search API.
/// Зачем: даёт агенту независимый от крупных экосистем индекс с приватным по духу API и бесплатным тиром.
/// Как: GET на /res/v1/web/search с заголовком X-Subscription-Token, разбор web.results в WebSearchResult.
/// Важно: Brave возвращает заголовок/URL/сниппет, но НЕ полный текст страницы — агент обязан опираться на сниппеты и честно говорить, если их мало.
/// </summary>
public sealed class BraveWebSearchProvider : IWebSearchProvider
{
    public const string HttpClientName = "brave-search";

    private const string SearchEndpoint = "https://api.search.brave.com/res/v1/web/search";
    private const int MaxSupportedCount = 20;
    private const int MaxDescriptionLength = 400;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<WebSearchOptions> _options;
    private readonly ILogger<BraveWebSearchProvider> _logger;

    public BraveWebSearchProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<WebSearchOptions> options,
        ILogger<BraveWebSearchProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured =>
        _options.Value.IsConfigured && WebSearchOptions.IsBrave(_options.Value.Provider);

    public async Task<WebSearchResponse> SearchAsync(
        string query,
        int count,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = (query ?? string.Empty).Trim();
        if (normalizedQuery.Length == 0)
        {
            return WebSearchResponse.Unavailable(normalizedQuery, "Empty query.");
        }

        if (!IsConfigured)
        {
            return WebSearchResponse.Unavailable(normalizedQuery, "Web search is not configured.");
        }

        var options = _options.Value;
        var requestedCount = Math.Clamp(
            count <= 0 ? options.MaxResults : count,
            1,
            MaxSupportedCount);

        var uri = BuildRequestUri(normalizedQuery, requestedCount, options.Country);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("X-Subscription-Token", options.ApiKey);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Ключ/квота — самые частые причины; не раскрываем сам ключ в логах и ответе.
                _logger.LogWarning(
                    "Brave web search failed with status {StatusCode} for a query of length {QueryLength}.",
                    (int)response.StatusCode,
                    normalizedQuery.Length);
                return WebSearchResponse.Unavailable(
                    normalizedQuery,
                    $"Search provider returned HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var results = ParseResults(payload, requestedCount);

            _logger.LogInformation(
                "Brave web search returned {ResultCount} result(s) for a query of length {QueryLength}.",
                results.Count,
                normalizedQuery.Length);

            return WebSearchResponse.Found(normalizedQuery, results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Brave web search request failed.");
            return WebSearchResponse.Unavailable(
                normalizedQuery,
                $"Search request failed: {exception.GetType().Name}.");
        }
    }

    private static Uri BuildRequestUri(string query, int count, string? country)
    {
        var parameters = new List<string>
        {
            $"q={Uri.EscapeDataString(query)}",
            $"count={count.ToString(CultureInfo.InvariantCulture)}",
        };

        if (!string.IsNullOrWhiteSpace(country))
        {
            parameters.Add($"country={Uri.EscapeDataString(country.Trim())}");
        }

        return new Uri($"{SearchEndpoint}?{string.Join('&', parameters)}");
    }

    /// <summary>Разбирает web.results; отсутствующие/битые поля пропускаются, а не роняют весь поиск.</summary>
    internal static IReadOnlyList<WebSearchResult> ParseResults(string? payload, int limit)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<WebSearchResult>();
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("web", out var web)
                || !web.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<WebSearchResult>();
            }

            var parsed = new List<WebSearchResult>();
            foreach (var item in results.EnumerateArray())
            {
                var url = ReadString(item, "url");
                var title = ReadString(item, "title");
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                parsed.Add(new WebSearchResult(
                    title.Trim(),
                    url.Trim(),
                    Truncate(StripTags(ReadString(item, "description") ?? string.Empty), MaxDescriptionLength),
                    ReadString(item, "page_age") ?? ReadString(item, "age")));

                if (parsed.Count >= limit)
                {
                    break;
                }
            }

            return parsed;
        }
        catch (JsonException)
        {
            return Array.Empty<WebSearchResult>();
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Brave подсвечивает совпадения тегами вроде &lt;strong&gt; — в текст для модели они не нужны.</summary>
    private static string StripTags(string value)
    {
        if (!value.Contains('<', StringComparison.Ordinal))
        {
            return value.Trim();
        }

        var builder = new System.Text.StringBuilder(value.Length);
        var insideTag = false;
        foreach (var character in value)
        {
            if (character == '<')
            {
                insideTag = true;
            }
            else if (character == '>')
            {
                insideTag = false;
            }
            else if (!insideTag)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
