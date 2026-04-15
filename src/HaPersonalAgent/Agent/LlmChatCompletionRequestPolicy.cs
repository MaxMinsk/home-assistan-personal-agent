using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: generic OpenAI-compatible request policy для provider-specific LLM extensions.
/// Зачем: reasoning/thinking controls должны применяться по execution plan, а не через hardcoded Moonshot-only policy в AgentRuntime.
/// Как: перед отправкой chat completions request аккуратно patch-ит JSON body, если provider capability объявил поддерживаемую schema.
/// </summary>
public sealed class LlmChatCompletionRequestPolicy : PipelinePolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LlmExecutionPlan _executionPlan;

    public LlmChatCompletionRequestPolicy(LlmExecutionPlan executionPlan)
    {
        _executionPlan = executionPlan;
    }

    public override void Process(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        PatchRequest(message, message.CancellationToken);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        await PatchRequestAsync(message, message.CancellationToken);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    public static bool TryPatchRequestJson(
        string json,
        LlmExecutionPlan executionPlan,
        out string patchedJson)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);

        patchedJson = json;
        if (!executionPlan.ShouldPatchChatCompletionRequest)
        {
            return false;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (rootNode is not JsonObject root)
        {
            return false;
        }

        var patched = TryApplyThinkingControl(root, executionPlan);
        if (!patched
            && TryApplyToolStepReasoningSafetyFallback(root, executionPlan))
        {
            patched = true;
        }

        if (!patched)
        {
            return false;
        }

        patchedJson = root.ToJsonString(JsonOptions);

        return true;
    }

    private static bool TryApplyToolStepReasoningSafetyFallback(
        JsonObject root,
        LlmExecutionPlan executionPlan)
    {
        if (!string.Equals(executionPlan.RequestedThinkingMode, "auto", StringComparison.Ordinal))
        {
            return false;
        }

        if (!executionPlan.Capabilities.RequiresReasoningContentRoundTripForToolCalls
            || executionPlan.Capabilities.ThinkingControlStyle != LlmThinkingControlStyle.OpenAiCompatibleThinkingObject)
        {
            return false;
        }

        if (!HasAssistantToolCallsWithoutReasoning(root))
        {
            return false;
        }

        root["thinking"] = new JsonObject
        {
            ["type"] = "disabled",
        };

        return true;
    }

    private static bool HasAssistantToolCallsWithoutReasoning(JsonObject root)
    {
        if (root["messages"] is not JsonArray messages)
        {
            return false;
        }

        foreach (var item in messages)
        {
            if (item is not JsonObject message)
            {
                continue;
            }

            if (!string.Equals(message["role"]?.GetValue<string>(), "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (message["tool_calls"] is not JsonArray toolCalls || toolCalls.Count == 0)
            {
                continue;
            }

            if (IsReasoningContentMissing(message["reasoning_content"]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReasoningContentMissing(JsonNode? reasoningContentNode)
    {
        if (reasoningContentNode is null)
        {
            return true;
        }

        if (reasoningContentNode is JsonArray array)
        {
            return array.Count == 0;
        }

        if (reasoningContentNode is JsonObject obj)
        {
            return obj.Count == 0;
        }

        if (reasoningContentNode is JsonValue value)
        {
            return !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text);
        }

        return false;
    }

    private static bool TryApplyThinkingControl(
        JsonObject root,
        LlmExecutionPlan executionPlan)
    {
        if (executionPlan.Capabilities.ThinkingControlStyle != LlmThinkingControlStyle.OpenAiCompatibleThinkingObject)
        {
            return false;
        }

        var type = executionPlan.EffectiveThinkingMode switch
        {
            LlmEffectiveThinkingMode.Disabled => "disabled",
            LlmEffectiveThinkingMode.Enabled => "enabled",
            _ => null,
        };

        if (type is null)
        {
            return false;
        }

        root["thinking"] = new JsonObject
        {
            ["type"] = type,
        };

        return true;
    }

    private void PatchRequest(PipelineMessage message, CancellationToken cancellationToken)
    {
        if (!ShouldPatch(message.Request) || message.Request.Content is null)
        {
            return;
        }

        using var stream = new MemoryStream();
        message.Request.Content.WriteTo(stream, cancellationToken);
        var json = Encoding.UTF8.GetString(stream.ToArray());

        if (TryPatchRequestJson(json, _executionPlan, out var patchedJson))
        {
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(patchedJson));
        }
    }

    private async ValueTask PatchRequestAsync(
        PipelineMessage message,
        CancellationToken cancellationToken)
    {
        if (!ShouldPatch(message.Request) || message.Request.Content is null)
        {
            return;
        }

        await using var stream = new MemoryStream();
        await message.Request.Content.WriteToAsync(stream, cancellationToken);
        var json = Encoding.UTF8.GetString(stream.ToArray());

        if (TryPatchRequestJson(json, _executionPlan, out var patchedJson))
        {
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(patchedJson));
        }
    }

    private static bool ShouldPatch(PipelineRequest request) =>
        string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
        && request.Uri is { } uri
        && uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);
}
