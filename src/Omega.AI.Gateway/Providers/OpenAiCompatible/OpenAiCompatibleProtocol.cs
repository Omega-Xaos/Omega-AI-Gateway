using System.Text.Json;
using System.Text.Json.Serialization;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;

namespace Omega.AI.Gateway.Providers.OpenAiCompatible;

internal static class OpenAiCompatibleProtocol
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static object BuildRequest(AiProfile profile, AiRequest request, string model, bool stream) => new
    {
        model,
        messages = request.Messages.Select(message => new { role = RoleName(message.Role), content = message.Content }).ToArray(),
        stream,
        response_format = request.ResponseFormat == AiResponseFormat.Json ? new { type = "json_object" } : null
    };

    public static AiResponse ParseResponse(JsonElement root, string requestedModel, System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        try
        {
            var choice = root.GetProperty("choices")[0];
            var text = choice.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
            var model = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() ?? requestedModel : requestedModel;
            AiUsage? usage = null;
            if (root.TryGetProperty("usage", out var usageElement))
            {
                usage = new AiUsage(GetInt(usageElement, "prompt_tokens"), GetInt(usageElement, "completion_tokens"), GetInt(usageElement, "total_tokens"));
            }

            root.TryGetProperty("id", out var idElement);
            return new AiResponse(text, model, choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() : null,
                usage, idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Provider completion response was malformed.");
        }
    }

    public static bool TryParseDelta(JsonElement root, out string? delta)
    {
        delta = null;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("delta", out var deltaObject) ||
            !deltaObject.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        delta = content.GetString();
        return !string.IsNullOrEmpty(delta);
    }

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static string RoleName(AiMessageRole role) => role switch
    {
        AiMessageRole.System => "system",
        AiMessageRole.Developer => "developer",
        AiMessageRole.User => "user",
        AiMessageRole.Assistant => "assistant",
        _ => throw new AiGatewayException(AiErrorCode.UnsupportedCapability, "Message role is unsupported.")
    };
}
