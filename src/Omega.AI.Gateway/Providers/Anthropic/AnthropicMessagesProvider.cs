using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Providers.OpenAiCompatible;
using Omega.AI.Gateway.Security;

namespace Omega.AI.Gateway.Providers.Anthropic;

public sealed class AnthropicMessagesProvider(HttpClient httpClient, IAiSecretResolver secrets) : IAiProvider
{
    private const string ApiVersion = "2026-10-21";
    private const int MaxModelsPages = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IAiSecretResolver _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));

    public string ProviderKind => "anthropic-messages";

    public async Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(AiProfile profile, CancellationToken cancellationToken = default)
    {
        var endpoint = OpenAiEndpointPolicy.Build(profile, "v1/models?limit=1000");
        var models = new List<AiModelInfo>();
        string? cursor = null;

        for (var page = 0; page < MaxModelsPages; page++)
        {
            var pageUri = cursor is null ? endpoint : AddQuery(endpoint, "after_id", cursor);
            using var request = await CreateRequestAsync(HttpMethod.Get, pageUri, profile, null, cancellationToken).ConfigureAwait(false);
            using var response = await SendHttpAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);

            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new AiGatewayException(AiErrorCode.MalformedResponse, "Anthropic model response was malformed.");

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    continue;
                var displayName = item.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String
                    ? display.GetString()
                    : null;
                models.Add(new AiModelInfo(id.GetString()!, displayName));
            }

            var hasMore = root.TryGetProperty("has_more", out var hasMoreElement) &&
                          hasMoreElement.ValueKind == JsonValueKind.True;
            if (!hasMore)
                return models;

            if (!root.TryGetProperty("last_id", out var lastId) || lastId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(lastId.GetString()) || string.Equals(cursor, lastId.GetString(), StringComparison.Ordinal))
                throw new AiGatewayException(AiErrorCode.MalformedResponse, "Anthropic model pagination cursor was missing or repeated.");

            cursor = lastId.GetString();
        }

        throw new AiGatewayException(AiErrorCode.MalformedResponse, "Anthropic model catalog exceeded the pagination safety limit.");
    }

    public async Task<AiConnectionTest> TestConnectionAsync(AiProfile profile, CancellationToken cancellationToken = default)
    {
        var models = await GetModelsAsync(profile, cancellationToken).ConfigureAwait(false);
        return new AiConnectionTest(true, "Anthropic model catalog is reachable.", models.FirstOrDefault()?.Id ?? profile.ModelId);
    }

    public async Task<AiResponse> SendAsync(AiProfile profile, AiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.ModelId ?? profile.ModelId;
        var (system, messages) = BuildMessages(request.Messages);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["messages"] = messages
        };
        if (!string.IsNullOrWhiteSpace(system))
            payload["system"] = system;

        var endpoint = OpenAiEndpointPolicy.Build(profile, "v1/messages");
        using var httpRequest = await CreateRequestAsync(HttpMethod.Post, endpoint, profile, payload, cancellationToken).ConfigureAwait(false);
        using var response = await SendHttpAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResponse(document.RootElement, model, response.Headers);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiProfile profile,
        AiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.ModelId ?? profile.ModelId;
        var (system, messages) = BuildMessages(request.Messages);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["messages"] = messages,
            ["stream"] = true
        };
        if (!string.IsNullOrWhiteSpace(system))
            payload["system"] = system;

        var endpoint = OpenAiEndpointPolicy.Build(profile, "v1/messages");
        using var requestMessage = await CreateRequestAsync(HttpMethod.Post, endpoint, profile, payload, cancellationToken).ConfigureAwait(false);
        requestMessage.Headers.Accept.Clear();
        requestMessage.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await SendHttpAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var completed = false;
        string? data = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..].TrimStart();
                data = data is null ? value : data + "\n" + value;
                continue;
            }

            if (line.Length != 0 || data is null)
                continue;

            using var eventDocument = ParseJson(data);
            var root = eventDocument.RootElement;
            data = null;
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                switch (type.GetString())
                {
                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("type", out var deltaType) && deltaType.GetString() == "text_delta" &&
                            delta.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            yield return new AiStreamEvent(AiStreamEventKind.TextDelta, text.GetString());
                        break;
                    case "message_stop":
                        completed = true;
                        break;
                    case "error":
                        throw new AiGatewayException(AiErrorCode.ProviderFailure, "Anthropic stream reported a provider error.");
                }
            }

            if (completed)
                break;
        }

        if (!completed)
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Anthropic stream ended before its completion event.");

        yield return new AiStreamEvent(AiStreamEventKind.Completed);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method, Uri endpoint, AiProfile profile, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, endpoint);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

        if (profile.AuthMode != AiAuthMode.SecretReference || string.IsNullOrWhiteSpace(profile.SecretReference))
        {
            request.Dispose();
            throw new AiGatewayException(profile.AuthMode == AiAuthMode.SecretReference ? AiErrorCode.Configuration : AiErrorCode.UnsupportedCapability,
                "Anthropic API credentials are not configured for this profile.");
        }

        var secret = await _secrets.ResolveAsync(profile.SecretReference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 8_192 || secret.Any(char.IsControl))
        {
            request.Dispose();
            throw new AiGatewayException(AiErrorCode.Authentication, "Anthropic API credential is unavailable.");
        }

        if (!request.Headers.TryAddWithoutValidation("x-api-key", secret))
        {
            request.Dispose();
            throw new AiGatewayException(AiErrorCode.Configuration, "Anthropic API-key header could not be added.");
        }
        return request;
    }

    private async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new AiGatewayException(AiErrorCode.Authorization, "Anthropic redirects are not followed for credential-bearing requests.");
            }
            if (response.RequestMessage?.RequestUri is { } actual && !SameOrigin(request.RequestUri!, actual))
            {
                response.Dispose();
                throw new AiGatewayException(AiErrorCode.Authorization, "Anthropic redirected a credential-bearing request to another origin.");
            }
            return response;
        }
        catch (AiGatewayException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException)
        {
            throw new AiGatewayException(AiErrorCode.NetworkFailure, "Anthropic network request failed.");
        }
    }

    private static (string? System, object[] Messages) BuildMessages(IReadOnlyList<AiMessage> messages)
    {
        if (messages is null || messages.Count == 0)
            throw new AiGatewayException(AiErrorCode.Configuration, "Anthropic request must contain at least one message.");

        var systemMessages = messages
            .Where(message => message.Role is AiMessageRole.System or AiMessageRole.Developer)
            .Select(message => message.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .ToArray();
        var conversation = new List<Dictionary<string, string>>();
        foreach (var message in messages)
        {
            if (message.Role is AiMessageRole.System or AiMessageRole.Developer)
                continue;
            var role = message.Role switch
            {
                AiMessageRole.User => "user",
                AiMessageRole.Assistant => "assistant",
                _ => throw new AiGatewayException(AiErrorCode.Configuration, "Anthropic request contains an unsupported message role.")
            };

            if (conversation.Count > 0 && conversation[^1]["role"] == role)
                conversation[^1]["content"] += "\n\n" + message.Content;
            else
                conversation.Add(new Dictionary<string, string> { ["role"] = role, ["content"] = message.Content });
        }

        if (conversation.Count == 0 || conversation[0]["role"] != "user")
            throw new AiGatewayException(AiErrorCode.Configuration, "Anthropic conversation must begin with a user message.");

        return (systemMessages.Length == 0 ? null : string.Join("\n\n", systemMessages), conversation.Cast<object>().ToArray());
    }

    private static AiResponse ParseResponse(JsonElement root, string requestedModel, System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Anthropic message response was malformed.");
        var text = string.Join("", content.EnumerateArray()
            .Where(block => block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text")
            .Where(block => block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
            .Select(block => block.GetProperty("text").GetString()));
        if (string.IsNullOrWhiteSpace(text))
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Anthropic message response contained no text.");

        var model = root.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String
            ? modelValue.GetString()!
            : requestedModel;
        var finishReason = root.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String
            ? stop.GetString()
            : null;
        int? inputTokens = null;
        int? outputTokens = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inputTokens = ReadInt(usage, "input_tokens");
            outputTokens = ReadInt(usage, "output_tokens");
        }
        int? total = inputTokens.HasValue && outputTokens.HasValue ? inputTokens.Value + outputTokens.Value : null;
        var requestId = headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null;
        if (requestId is null && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            requestId = id.GetString();
        return new AiResponse(text, model, finishReason,
            inputTokens.HasValue || outputTokens.HasValue ? new AiUsage(inputTokens, outputTokens, total) : null,
            requestId);
    }

    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static Uri AddQuery(Uri uri, string name, string value)
    {
        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        var encoded = $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
        builder.Query = string.IsNullOrEmpty(existing) ? encoded : existing + "&" + encoded;
        return builder.Uri;
    }

    private static bool SameOrigin(Uri requested, Uri actual) =>
        string.Equals(requested.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requested.Host, actual.Host, StringComparison.OrdinalIgnoreCase) && requested.Port == actual.Port;

    private static Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => AiErrorCode.Authentication,
            HttpStatusCode.Forbidden => AiErrorCode.Authorization,
            HttpStatusCode.TooManyRequests => AiErrorCode.RateLimit,
            _ => AiErrorCode.ProviderFailure
        };
        throw new AiGatewayException(code, $"Anthropic returned HTTP {(int)response.StatusCode}.");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Anthropic returned invalid JSON.");
        }
    }

    private static JsonDocument ParseJson(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { throw new AiGatewayException(AiErrorCode.MalformedResponse, "Anthropic stream contained invalid JSON."); }
    }
}
