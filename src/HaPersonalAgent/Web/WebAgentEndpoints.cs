using System.Globalization;
using System.Text.Json;
using HaPersonalAgent.Autonomous;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Memory;
using HaPersonalAgent.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Web;

/// <summary>
/// Что: HTTP-эндпоинты управления автономными агентами для Web UI (HPA-033).
/// Зачем: панель должна уметь всё, что обещал эпик, — создать агента, отредактировать, поставить на паузу, запустить сейчас, прочитать сводки и ответить на вопрос.
/// Как: тонкий слой над AutonomousAgentService/IAutonomousAgentRepository с проекцией домена в web-DTO; ответ пользователя кладётся в ту же очередь, что и Telegram-reply.
/// </summary>
public static class WebAgentEndpoints
{
    private const int DefaultRunHistoryLimit = 25;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAgentManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/capabilities", HandleCapabilities);
        endpoints.MapGet("/api/agents", HandleListAsync);
        endpoints.MapPost("/api/agents", HandleCreateAsync);
        endpoints.MapPost("/api/agents/pause-all", HandlePauseAllAsync);
        endpoints.MapGet("/api/agents/{agentId}", HandleGetAsync);
        endpoints.MapPut("/api/agents/{agentId}", HandleUpdateAsync);
        endpoints.MapDelete("/api/agents/{agentId}", HandleDeleteAsync);
        endpoints.MapPost("/api/agents/{agentId}/status", HandleStatusAsync);
        endpoints.MapPost("/api/agents/{agentId}/run", HandleRunNowAsync);
        endpoints.MapGet("/api/agents/{agentId}/runs", HandleRunsAsync);
        endpoints.MapPost("/api/agents/{agentId}/reply", HandleReplyAsync);

        return endpoints;
    }

    /// <summary>
    /// Сообщает UI, какие возможности реально настроены, чтобы форма/шаблоны честно помечали то, что не заработает
    /// без ключа (веб-поиск) или без настроенной памяти.
    /// </summary>
    private static IResult HandleCapabilities(
        IWebSearchProvider? webSearch,
        IOptions<MemoryMcpOptions> memoryOptions) =>
        Results.Json(
            new
            {
                webSearchConfigured = webSearch?.IsConfigured ?? false,
                memoryConfigured = memoryOptions.Value.IsConfigured,
            },
            JsonOptions);

    private static async Task<IResult> HandleListAsync(
        AutonomousAgentService agents,
        IAutonomousAgentRepository repository,
        CancellationToken cancellationToken)
    {
        var definitions = await agents.ListAsync(cancellationToken);
        var summaries = new List<AgentSummaryResponse>(definitions.Count);

        foreach (var definition in definitions)
        {
            var hasRunningRun = await repository.HasRunningRunAsync(definition.Id, cancellationToken);
            var pendingReplies = await repository.GetPendingRepliesAsync(definition.Id, cancellationToken);
            var continuity = await repository.GetContinuityAsync(definition.Id, cancellationToken);

            summaries.Add(new AgentSummaryResponse(
                definition.Id,
                definition.Name,
                definition.Status.ToString(),
                definition.ScheduleKind.ToString(),
                ToIso(definition.NextRunUtc),
                ToIso(definition.LastRunUtc),
                hasRunningRun,
                pendingReplies.Count,
                CountOpenQuestions(continuity?.OpenQuestions)));
        }

        return Results.Json(summaries, JsonOptions);
    }

    /// <summary>
    /// Глобальный стоп-кран: разом ставит на паузу всех активных агентов.
    /// Идущий прямо сейчас запуск он не обрывает — тот доработает и завершится, но следующего не будет.
    /// </summary>
    private static async Task<IResult> HandlePauseAllAsync(
        AutonomousAgentService agents,
        CancellationToken cancellationToken)
    {
        var definitions = await agents.ListAsync(cancellationToken);
        var paused = 0;

        foreach (var definition in definitions.Where(d => d.Status == AutonomousAgentStatus.Active))
        {
            await agents.SetStatusAsync(definition.Id, AutonomousAgentStatus.Paused, cancellationToken);
            paused++;
        }

        return Results.Json(new { ok = true, paused }, JsonOptions);
    }

    private static async Task<IResult> HandleGetAsync(
        string agentId,
        AutonomousAgentService agents,
        IAutonomousAgentRepository repository,
        CancellationToken cancellationToken)
    {
        var definition = await agents.GetAsync(agentId, cancellationToken);
        if (definition is null)
        {
            return Results.NotFound(new { error = "Agent not found." });
        }

        var continuity = await repository.GetContinuityAsync(agentId, cancellationToken);
        var hasRunningRun = await repository.HasRunningRunAsync(agentId, cancellationToken);
        var pendingReplies = await repository.GetPendingRepliesAsync(agentId, cancellationToken);

        return Results.Json(ToDetail(definition, continuity, hasRunningRun, pendingReplies.Count), JsonOptions);
    }

    private static async Task<IResult> HandleCreateAsync(
        AgentUpsertRequest? request,
        AutonomousAgentService agents,
        IAutonomousAgentRepository repository,
        CancellationToken cancellationToken)
    {
        if (!TryValidateUpsert(request, out var name, out var mission, out var scheduleKind, out var error))
        {
            return Results.BadRequest(new { error });
        }

        try
        {
            var created = await agents.CreateAsync(
                name,
                mission,
                scheduleKind,
                request!.ScheduleExpression,
                ToToolScope(request.ToolScope),
                request.DeliveryTelegramChatId,
                cancellationToken);

            var continuity = await repository.GetContinuityAsync(created.Id, cancellationToken);
            return Results.Json(ToDetail(created, continuity, false, 0), JsonOptions);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> HandleUpdateAsync(
        string agentId,
        AgentUpsertRequest? request,
        AutonomousAgentService agents,
        IAutonomousAgentRepository repository,
        CancellationToken cancellationToken)
    {
        if (!TryValidateUpsert(request, out var name, out var mission, out var scheduleKind, out var error))
        {
            return Results.BadRequest(new { error });
        }

        try
        {
            var existing = await agents.GetAsync(agentId, cancellationToken);
            if (existing is null)
            {
                return Results.NotFound(new { error = "Agent not found." });
            }

            var updated = await agents.UpdateAsync(
                agentId,
                name,
                mission,
                scheduleKind,
                request!.ScheduleExpression,
                ToToolScope(request.ToolScope) ?? existing.ToolScope,
                request.DeliveryTelegramChatId,
                cancellationToken);

            if (updated is null)
            {
                return Results.NotFound(new { error = "Agent not found." });
            }

            var continuity = await repository.GetContinuityAsync(agentId, cancellationToken);
            var hasRunningRun = await repository.HasRunningRunAsync(agentId, cancellationToken);
            var pendingReplies = await repository.GetPendingRepliesAsync(agentId, cancellationToken);

            return Results.Json(ToDetail(updated, continuity, hasRunningRun, pendingReplies.Count), JsonOptions);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> HandleDeleteAsync(
        string agentId,
        AutonomousAgentService agents,
        CancellationToken cancellationToken)
    {
        var deleted = await agents.DeleteAsync(agentId, cancellationToken);
        return deleted
            ? Results.Json(new { ok = true }, JsonOptions)
            : Results.NotFound(new { error = "Agent not found." });
    }

    private static async Task<IResult> HandleStatusAsync(
        string agentId,
        AgentStatusRequest? request,
        AutonomousAgentService agents,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AutonomousAgentStatus>(request?.Status, ignoreCase: true, out var status))
        {
            return Results.BadRequest(new { error = "status must be 'active' or 'paused'." });
        }

        var updated = await agents.SetStatusAsync(agentId, status, cancellationToken);
        return updated is null
            ? Results.NotFound(new { error = "Agent not found." })
            : Results.Json(new { ok = true, status = updated.Status.ToString() }, JsonOptions);
    }

    private static async Task<IResult> HandleRunNowAsync(
        string agentId,
        AutonomousAgentService agents,
        CancellationToken cancellationToken)
    {
        // Запуск не выполняется здесь: сдвигаем срок, а исполняет планировщик — один путь исполнения, без гонок.
        var requested = await agents.RequestRunNowAsync(agentId, cancellationToken);
        return requested
            ? Results.Json(new { ok = true }, JsonOptions)
            : Results.NotFound(new { error = "Agent not found." });
    }

    private static async Task<IResult> HandleRunsAsync(
        string agentId,
        int? limit,
        AutonomousAgentService agents,
        CancellationToken cancellationToken)
    {
        var runs = await agents.ListRunsAsync(agentId, limit ?? DefaultRunHistoryLimit, cancellationToken);
        var responses = runs
            .Select(run => new AgentRunResponse(
                run.Id,
                run.Status.ToString(),
                ToIso(run.StartedUtc)!,
                ToIso(run.FinishedUtc),
                run.Summary,
                ParseQuestions(run.QuestionsJson),
                run.Error,
                run.ToolCallCount))
            .ToList();

        return Results.Json(responses, JsonOptions);
    }

    private static async Task<IResult> HandleReplyAsync(
        string agentId,
        AgentReplyRequest? request,
        AutonomousAgentService agents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Text))
        {
            return Results.BadRequest(new { error = "text is required." });
        }

        var entry = await agents.RecordReplyAsync(
            agentId,
            request.Text,
            AutonomousAgentReplySource.Web,
            runId: null,
            cancellationToken);

        return entry is null
            ? Results.NotFound(new { error = "Agent not found." })
            : Results.Json(new { ok = true, queuedAt = ToIso(entry.ReceivedUtc) }, JsonOptions);
    }

    private static bool TryValidateUpsert(
        AgentUpsertRequest? request,
        out string name,
        out string mission,
        out AutonomousAgentScheduleKind scheduleKind,
        out string error)
    {
        name = request?.Name?.Trim() ?? string.Empty;
        mission = request?.Mission?.Trim() ?? string.Empty;
        scheduleKind = AutonomousAgentScheduleKind.Manual;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(mission))
        {
            error = "mission is required.";
            return false;
        }

        if (!Enum.TryParse(request?.ScheduleKind, ignoreCase: true, out scheduleKind))
        {
            error = "scheduleKind must be one of: manual, hourly, daily, weekly, cron.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static AutonomousAgentToolScope? ToToolScope(AgentToolScopeDto? dto) =>
        dto is null
            ? null
            : AutonomousAgentToolScope.Create(
                dto.AllowHomeAssistantRead,
                dto.AllowWebSearch,
                dto.AllowMemoryRead,
                dto.AllowMemoryWrite,
                dto.MaxDurableFactsPerRun);

    private static AgentDetailResponse ToDetail(
        AutonomousAgentDefinition definition,
        AutonomousAgentContinuity? continuity,
        bool hasRunningRun,
        int pendingReplyCount) =>
        new(
            definition.Id,
            definition.Name,
            definition.Mission,
            definition.Status.ToString(),
            definition.ScheduleKind.ToString(),
            definition.ScheduleExpression,
            definition.DeliveryTelegramChatId,
            new AgentToolScopeDto(
                definition.ToolScope.AllowHomeAssistantRead,
                definition.ToolScope.AllowWebSearch,
                definition.ToolScope.AllowMemoryRead,
                definition.ToolScope.AllowMemoryWrite,
                definition.ToolScope.MaxDurableFactsPerRun),
            ToIso(definition.NextRunUtc),
            ToIso(definition.LastRunUtc),
            ToIso(definition.CreatedUtc)!,
            ToIso(definition.UpdatedUtc)!,
            hasRunningRun,
            pendingReplyCount,
            continuity?.Focus,
            continuity?.OpenQuestions,
            continuity?.CapsuleNoteKey,
            ToIso(continuity?.CapsuleUpdatedUtc));

    private static IReadOnlyList<string> ParseQuestions(string? questionsJson)
    {
        if (string.IsNullOrWhiteSpace(questionsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(questionsJson, JsonOptions) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static int CountOpenQuestions(string? openQuestions) =>
        string.IsNullOrWhiteSpace(openQuestions)
            ? 0
            : openQuestions.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static string? ToIso(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);
}
