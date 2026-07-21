using System.Collections.Concurrent;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: per-run хранилище reasoning-текста модели, привязанного к id конкретного tool_call.
/// Зачем: HPA-041 follow-up — чтобы thinking оставался включённым на continuation tool-шагах, провайдеру нужно
/// вернуть его же reasoning_content для assistant-сообщений с tool_calls. M.E.AI парсит reasoning из ответа, но
/// НЕ сериализует его в исходящий запрос, поэтому захват (capture) и вписывание (inject) разнесены по слоям и
/// общаются через это хранилище: capture-клиент кладёт reasoning по callId, request-политика достаёт его по тому же callId.
/// Как: потокобезопасная map callId -> reasoning; ключи стабильны (FunctionCallContent.CallId == tool_calls[].id на проводе).
/// </summary>
public sealed class ToolCallReasoningStore
{
    private readonly ConcurrentDictionary<string, string> _reasoningByToolCallId = new(StringComparer.Ordinal);

    public void Capture(string toolCallId, string reasoning)
    {
        if (string.IsNullOrWhiteSpace(toolCallId) || string.IsNullOrWhiteSpace(reasoning))
        {
            return;
        }

        _reasoningByToolCallId[toolCallId] = reasoning;
    }

    public bool TryGet(string toolCallId, out string reasoning)
    {
        if (!string.IsNullOrWhiteSpace(toolCallId)
            && _reasoningByToolCallId.TryGetValue(toolCallId, out var value))
        {
            reasoning = value;
            return true;
        }

        reasoning = string.Empty;
        return false;
    }
}
