using System.Text;
using System.Text.Json;
using HaPersonalAgent.Agent;
using HaPersonalAgent.Dialogue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HaPersonalAgent.Web;

/// <summary>
/// Что: HTTP-эндпоинты диалога с основным conversation-агентом поверх transport-agnostic DialogueService (транспорт "web").
/// Зачем: Web UI (HPA-027) и другие HTTP-клиенты должны вести диалог через то же ядро, что и Telegram, без дублирования логики истории/памяти/runtime.
/// Как: turn — синхронный ход; stream — тот же ход c SSE-стримингом reasoning через OnReasoningUpdate; context — снимок контекста; reset — очистка контекста.
/// </summary>
public static class WebDialogueEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAgentDialogueEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/dialogue/turn", HandleTurnAsync);
        endpoints.MapPost("/api/dialogue/stream", HandleStreamAsync);
        endpoints.MapGet("/api/dialogue/context", HandleContextAsync);
        endpoints.MapGet("/api/dialogue/summary", HandleSummaryAsync);
        endpoints.MapPost("/api/dialogue/reset", HandleResetAsync);

        return endpoints;
    }

    private static async Task<IResult> HandleTurnAsync(
        DialogueTurnRequest? request,
        DialogueService dialogue,
        CancellationToken cancellationToken)
    {
        if (!TryValidateTurn(request, out var conversationId, out var text, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var conversation = WebDialogueTransport.CreateConversation(conversationId, request!.ParticipantId);
        var profile = WebDialogueTransport.ResolveExecutionProfile(request.Profile);
        var dialogueRequest = DialogueRequest.Create(conversation, text, correlationId: null, profile);
        var response = await dialogue.SendUserMessageAsync(dialogueRequest, cancellationToken);

        return Results.Json(
            new DialogueTurnResponse(response.Text, response.CorrelationId, response.IsConfigured),
            JsonOptions);
    }

    private static async Task<IResult> HandleStreamAsync(
        DialogueTurnRequest? request,
        DialogueService dialogue,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryValidateTurn(request, out var conversationId, out var text, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var conversation = WebDialogueTransport.CreateConversation(conversationId, request!.ParticipantId);
        var profile = WebDialogueTransport.ResolveExecutionProfile(request.Profile);

        var response = httpContext.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        // Reasoning-дельты и финальный ответ пишутся в один поток ответа; сериализуем доступ, чтобы избежать чересполосицы.
        var writeSync = new SemaphoreSlim(1, 1);

        async Task OnReasoningUpdateAsync(AgentRuntimeReasoningUpdate update, CancellationToken token)
        {
            if (string.IsNullOrEmpty(update.TextDelta))
            {
                return;
            }

            await writeSync.WaitAsync(token);
            try
            {
                await WriteSseEventAsync(response, "reasoning", update.TextDelta, token);
            }
            finally
            {
                writeSync.Release();
            }
        }

        var dialogueRequest = DialogueRequest.Create(
            conversation,
            text,
            correlationId: null,
            profile,
            OnReasoningUpdateAsync);
        var result = await dialogue.SendUserMessageAsync(dialogueRequest, cancellationToken);

        await writeSync.WaitAsync(cancellationToken);
        try
        {
            var payload = JsonSerializer.Serialize(
                new DialogueTurnResponse(result.Text, result.CorrelationId, result.IsConfigured),
                JsonOptions);
            await WriteSseEventAsync(response, "message", payload, cancellationToken);
        }
        finally
        {
            writeSync.Release();
        }

        return Results.Empty;
    }

    private static async Task<IResult> HandleContextAsync(
        string? conversationId,
        string? participantId,
        DialogueService dialogue,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return Results.BadRequest(new { error = "conversationId is required." });
        }

        var conversation = WebDialogueTransport.CreateConversation(conversationId, participantId);
        var snapshot = await dialogue.GetContextSnapshotAsync(conversation, cancellationToken);

        return Results.Json(snapshot, JsonOptions);
    }

    private static async Task<IResult> HandleSummaryAsync(
        string? conversationId,
        string? participantId,
        DialogueService dialogue,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return Results.BadRequest(new { error = "conversationId is required." });
        }

        var conversation = WebDialogueTransport.CreateConversation(conversationId, participantId);
        var summary = await dialogue.GetPersistedSummaryAsync(conversation, cancellationToken);

        var response = summary is null
            ? new DialogueSummaryResponse(false, null, 0, null, 0)
            : new DialogueSummaryResponse(
                true,
                summary.Summary,
                summary.SummaryVersion,
                summary.UpdatedAtUtc.ToString("O"),
                summary.SourceLastMessageId);

        return Results.Json(response, JsonOptions);
    }

    private static async Task<IResult> HandleResetAsync(
        DialogueResetRequest? request,
        DialogueService dialogue,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.ConversationId))
        {
            return Results.BadRequest(new { error = "conversationId is required." });
        }

        var conversation = WebDialogueTransport.CreateConversation(request.ConversationId, request.ParticipantId);
        await dialogue.ResetAsync(conversation, cancellationToken);

        return Results.Json(new DialogueResetResponse(true), JsonOptions);
    }

    private static bool TryValidateTurn(
        DialogueTurnRequest? request,
        out string conversationId,
        out string text,
        out string error)
    {
        conversationId = request?.ConversationId?.Trim() ?? string.Empty;
        text = request?.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            error = "conversationId is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "text is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static async Task WriteSseEventAsync(
        HttpResponse response,
        string eventName,
        string data,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("event: ").Append(eventName).Append('\n');

        // SSE требует префикса "data: " на каждой строке; многострочный текст reasoning бьём построчно.
        foreach (var line in data.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            builder.Append("data: ").Append(line).Append('\n');
        }

        builder.Append('\n');
        await response.WriteAsync(builder.ToString(), cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
