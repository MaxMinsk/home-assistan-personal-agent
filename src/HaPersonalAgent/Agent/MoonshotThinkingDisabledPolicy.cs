using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: pipeline policy для OpenAI-compatible Moonshot/Kimi chat completions.
/// Зачем: kimi-k2.5 включает thinking по умолчанию, а OpenAI/Microsoft.Extensions.AI tool-call history не содержит `reasoning_content`, из-за чего Moonshot может вернуть HTTP 400.
/// Как: перед отправкой HTTP request добавляет в JSON body `thinking: { "type": "disabled" }`, не меняя остальную сериализацию SDK.
/// </summary>
public sealed class MoonshotThinkingDisabledPolicy : PipelinePolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public static bool TryPatchRequestJson(string json, out string patchedJson)
    {
        patchedJson = json;

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

        root["thinking"] = new JsonObject
        {
            ["type"] = "disabled",
        };
        patchedJson = root.ToJsonString(JsonOptions);

        return true;
    }

    private static void PatchRequest(PipelineMessage message, CancellationToken cancellationToken)
    {
        if (!ShouldPatch(message.Request) || message.Request.Content is null)
        {
            return;
        }

        using var stream = new MemoryStream();
        message.Request.Content.WriteTo(stream, cancellationToken);
        var json = Encoding.UTF8.GetString(stream.ToArray());

        if (TryPatchRequestJson(json, out var patchedJson))
        {
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(patchedJson));
        }
    }

    private static async ValueTask PatchRequestAsync(
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

        if (TryPatchRequestJson(json, out var patchedJson))
        {
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(patchedJson));
        }
    }

    private static bool ShouldPatch(PipelineRequest request) =>
        string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
        && request.Uri is { } uri
        && uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);
}
