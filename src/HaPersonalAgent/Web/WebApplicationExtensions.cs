using HaPersonalAgent.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Web;

/// <summary>
/// Что: middleware и эндпоинты встроенного веб-хоста (ingress path-base, авторизация, health, раздача SPA).
/// Зачем: держим весь веб-pipeline в одном месте, чтобы Program.cs оставался тонким, а транспорт-agnostic ядро не знало про HTTP.
/// Как: ставит PathBase из заголовка HA Ingress, закрывает прямой доступ api-token'ом (кроме /api/health), отдаёт версионированный /api/health и статический SPA с fallback на index.html.
/// </summary>
public static class WebApplicationExtensions
{
    private const string IngressPathHeader = "X-Ingress-Path";
    private const string ApiTokenHeader = "X-Api-Token";
    private const string HealthPath = "/api/health";

    public static WebApplication MapAgentWebEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<IOptions<WebHostOptions>>().Value;

        // HA Ingress проксирует запросы под префиксом /api/hassio_ingress/<token>/ и передаёт его в X-Ingress-Path.
        // Ставим PathBase, чтобы относительные ссылки и статические ассеты SPA резолвились под этим префиксом.
        app.Use(async (context, next) =>
        {
            var ingressPath = context.Request.Headers[IngressPathHeader].FirstOrDefault();
            if (!string.IsNullOrEmpty(ingressPath))
            {
                context.Request.PathBase = ingressPath;
            }

            await next();
        });

        // Авторизация: за Ingress аутентифицирует HA; прямой (проброшенный) порт закрываем api-token'ом. /api/health открыт всегда для liveness.
        app.Use(async (context, next) =>
        {
            if (IsHealthRequest(context))
            {
                await next();
                return;
            }

            var hasIngressHeader = context.Request.Headers.ContainsKey(IngressPathHeader);
            var providedToken = ExtractProvidedToken(context);
            if (!WebRequestAuthorizer.IsAuthorized(hasIngressHeader, providedToken, options.ApiToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized.");
                return;
            }

            await next();
        });

        if (options.Enabled)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }

        app.MapGet(HealthPath, () => Results.Json(WebHealthSnapshot.Create(options.Enabled)));

        // JSON dialogue API поверх того же хоста (HPA-026): доступно и без SPA-бандла, всегда под auth-gate.
        app.MapAgentDialogueEndpoints();

        // Управление автономными агентами для панели (HPA-033).
        app.MapAgentManagementEndpoints();

        if (options.Enabled)
        {
            // Любой не-API путь отдаёт index.html — это точка входа SPA (клиентский роутинг подключим в HPA-027).
            app.MapFallbackToFile("index.html");
        }

        return app;
    }

    private static bool IsHealthRequest(HttpContext context) =>
        context.Request.Path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase);

    private static string? ExtractProvidedToken(HttpContext context)
    {
        var apiTokenHeader = context.Request.Headers[ApiTokenHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(apiTokenHeader))
        {
            return apiTokenHeader.Trim();
        }

        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        const string bearerPrefix = "Bearer ";
        if (!string.IsNullOrWhiteSpace(authorization) &&
            authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return authorization[bearerPrefix.Length..].Trim();
        }

        return null;
    }
}
