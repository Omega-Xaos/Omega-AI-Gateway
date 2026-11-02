using System.Text.Json;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;

namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

internal static class ResponsesProtocol
{
    public static object BuildRequest(AiProfile profile, AiRequest request, string model)
    {
        if (request.ResponseFormat != AiResponseFormat.Text)
        {
            throw new AiGatewayException(AiErrorCode.UnsupportedCapability, "ChatGPT plan preview does not support this response format through the Gateway.");
        }

        var instructions = string.Join("\n\n", request.Messages
            .Where(message => message.Role is AiMessageRole.System or AiMessageRole.Developer)
            .Select(message => message.Content));
        var input = request.Messages
            .Where(message => message.Role is AiMessageRole.User or AiMessageRole.Assistant)
            .Select(message => new { role = message.Role == AiMessageRole.User ? "user" : "assistant", content = message.Content })
            .ToArray();
        if (input.Length == 0)
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "ChatGPT Responses input must include a user or assistant message.");
        }

        return new
        {
            model,
            input,
            instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions,
            store = false,
            stream = true
        };
    }

    public static string? ReadDelta(JsonElement root) =>
        root.TryGetProperty("type", out var type) && type.GetString() == "response.output_text.delta" &&
        root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String
            ? delta.GetString()
            : null;

    public static AiResponse ReadCompleted(JsonElement root, string fallbackModel, string text)
    {
        var response = root.TryGetProperty("response", out var nested) ? nested : root;
        var model = response.TryGetProperty("model", out var modelValue) ? modelValue.GetString() ?? fallbackModel : fallbackModel;
        AiUsage? usage = null;
        if (response.TryGetProperty("usage", out var usageValue))
        {
            var input = usageValue.TryGetProperty("input_tokens", out var inputValue) && inputValue.TryGetInt32(out var inputCount) ? inputCount : (int?)null;
            var output = usageValue.TryGetProperty("output_tokens", out var outputValue) && outputValue.TryGetInt32(out var outputCount) ? outputCount : (int?)null;
            usage = new AiUsage(input, output, input.HasValue && output.HasValue ? input + output : null);
        }

        var id = response.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        return new AiResponse(text, model, "completed", usage, id);
    }

    public static AiGatewayException ReadFailure(JsonElement root)
    {
        var response = root.TryGetProperty("response", out var nested) ? nested : root;
        var error = response.TryGetProperty("error", out var inner) ? inner : root.TryGetProperty("error", out inner) ? inner : default;
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
        var mapped = code is "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_usage_unavailable"
            ? AiErrorCode.RateLimit
            : AiErrorCode.ProviderFailure;
        return new AiGatewayException(mapped, mapped == AiErrorCode.RateLimit
            ? "ChatGPT plan usage limit was reached or is unavailable."
            : "ChatGPT Responses request failed.");
    }
}
