using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Providers.OpenAiCompatible;
using Omega.AI.Gateway.Security;

namespace Omega.AI.Gateway.Providers.GoogleGemini;

public sealed class GoogleGeminiProvider : IAiProvider
{
    private const string ApiKeyHeader = "x-goog-api-key";
    private const int MaximumModelPages = 50;
    private readonly HttpClient _httpClient;
    private readonly IAiSecretResolver _secrets;

    public GoogleGeminiProvider(HttpClient httpClient, IAiSecretResolver secrets)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public string ProviderKind => "google-gemini";

    public async Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(AiProfile profile, CancellationToken cancellationToken = default)
    {
        var endpoint = OpenAiEndpointPolicy.Build(profile, "v1beta/models?pageSize=100");
        var models = new List<AiModelInfo>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;

        for (var page = 0; page < MaximumModelPages; page++)
        {
            var pageUri = pageToken is null ? endpoint : AddPageToken(endpoint, pageToken);
            using var request = await CreateRequestAsync(HttpMethod.Get, pageUri, profile, null, cancellationToken).ConfigureAwait(false);
            using var response = await SendHttpAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("models", out var modelArray) || modelArray.ValueKind != JsonValueKind.Array)
            {
                throw new AiGatewayException(AiErrorCode.MalformedResponse, "Gemini model response was malformed.");
            }

            foreach (var model in modelArray.EnumerateArray())
            {
                if (!SupportsGenerateContent(model) || !TryReadString(model, "name", out var modelName))
                    continue;

                var id = NormalizeModelId(modelName);
                var displayName = TryReadString(model, "displayName", out var display) ? display : null;
                var description = TryReadString(model, "description", out var details) ? details : null;
                models.Add(new AiModelInfo(id, displayName, description));
            }

            pageToken = TryReadString(document.RootElement, "nextPageToken", out var next) ? next : null;
            if (string.IsNullOrWhiteSpace(pageToken))
                return models;

            if (!seenTokens.Add(pageToken))
                throw new AiGatewayException(AiErrorCode.MalformedResponse, "Gemini model response repeated a pagination token.");
        }

        throw new AiGatewayException(AiErrorCode.ProviderFailure, "Gemini model catalog exceeded the supported page limit.");
    }

    public async Task<AiConnectionTest> TestConnectionAsync(AiProfile profile, CancellationToken cancellationToken = default)
    {
        var models = await GetModelsAsync(profile, cancellationToken).ConfigureAwait(false);
        return new AiConnectionTest(true, "Gemini model catalog is reachable.", models.FirstOrDefault()?.Id ?? profile.ModelId);
    }

    public async Task<AiResponse> SendAsync(AiProfile profile, AiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modelId = request.ModelId ?? profile.ModelId;
        var endpoint = BuildGenerateEndpoint(profile, modelId, stream: false);
        var payload = BuildRequestBody(request);
        using var httpRequest = await CreateRequestAsync(HttpMethod.Post, endpoint, profile, payload, cancellationToken).ConfigureAwait(false);
        using var response = await SendHttpAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResponse(document.RootElement, modelId);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiProfile profile,
        AiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modelId = request.ModelId ?? profile.ModelId;
        var endpoint = BuildGenerateEndpoint(profile, modelId, stream: true);
        using var httpRequest = await CreateRequestAsync(HttpMethod.Post, endpoint, profile, BuildRequestBody(request), cancellationToken).ConfigureAwait(false);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await SendHttpAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var completed = false;
        await foreach (var data in ReadEventDataAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (data == "[DONE]")
            {
                completed = true;
                break;
            }

            using var document = ParseJson(data);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out _))
                throw new AiGatewayException(AiErrorCode.ProviderFailure, "Gemini stream returned an error.");

            foreach (var text in ReadCandidateText(root))
                yield return new AiStreamEvent(AiStreamEventKind.TextDelta, text);

            if (HasFinishReason(root))
                completed = true;
        }

        if (!completed)
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Gemini stream ended before a completion marker.");

        yield return new AiStreamEvent(AiStreamEventKind.Completed);
    }

    private static object BuildRequestBody(AiRequest request)
    {
        var systemText = string.Join("\n\n", request.Messages
            .Where(message => message.Role is AiMessageRole.System or AiMessageRole.Developer)
            .Select(message => message.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content)));

        var contents = request.Messages
            .Where(message => message.Role is AiMessageRole.User or AiMessageRole.Assistant)
            .Select(message => new
            {
                role = message.Role == AiMessageRole.Assistant ? "model" : "user",
                parts = new[] { new { text = message.Content } }
            })
            .ToArray();

        if (contents.Length == 0)
            throw new AiGatewayException(AiErrorCode.Configuration, "Gemini requests must contain at least one user or assistant message.");

        return new
        {
            systemInstruction = string.IsNullOrWhiteSpace(systemText)
                ? null
                : new { parts = new[] { new { text = systemText } } },
            contents,
            generationConfig = request.ResponseFormat == AiResponseFormat.Json
                ? new { responseMimeType = "application/json" }
                : null
        };
    }

    private static Uri BuildGenerateEndpoint(AiProfile profile, string modelId, bool stream)
    {
        var normalizedModel = NormalizeModelId(modelId);
        var modelSuffix = normalizedModel["models/".Length..];
        var operation = stream ? "streamGenerateContent?alt=sse" : "generateContent";
        return OpenAiEndpointPolicy.Build(profile, $"v1beta/models/{Uri.EscapeDataString(modelSuffix)}:{operation}");
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        Uri endpoint,
        AiProfile profile,
        object? body,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, endpoint);
        try
        {
            if (body is not null)
                request.Content = JsonContent.Create(body);

            if (profile.AuthMode != AiAuthMode.SecretReference || string.IsNullOrWhiteSpace(profile.SecretReference))
                throw new AiGatewayException(AiErrorCode.UnsupportedCapability, "Gemini requires an API key stored as a secret reference.");

            var secret = await _secrets.ResolveAsync(profile.SecretReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(secret) || secret.Length > 8_192 || secret.Any(char.IsControl))
                throw new AiGatewayException(AiErrorCode.Authentication, "Gemini API credential is unavailable.");

            if (!request.Headers.TryAddWithoutValidation(ApiKeyHeader, secret))
                throw new AiGatewayException(AiErrorCode.Configuration, "Gemini API-key header could not be added.");

            request.Headers.Accept.ParseAdd(method == HttpMethod.Post ? "application/json" : "application/json");
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new AiGatewayException(AiErrorCode.Authorization, "Gemini redirects are disabled for credential-bearing requests.");
            }

            if (response.RequestMessage?.RequestUri is { } actual && !SameOrigin(request.RequestUri!, actual))
            {
                response.Dispose();
                throw new AiGatewayException(AiErrorCode.Authorization, "Gemini redirected a credential-bearing request to another origin.");
            }

            return response;
        }
        catch (AiGatewayException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new AiGatewayException(AiErrorCode.NetworkFailure, "Gemini network request failed.");
        }
    }

    private static AiResponse ParseResponse(JsonElement root, string requestedModel)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Gemini response did not contain a candidate.");

        var text = string.Concat(ReadCandidateText(root));
        var finishReason = ReadFirstCandidateString(root, "finishReason");
        var modelId = TryReadString(root, "modelVersion", out var version) ? version : requestedModel;
        AiUsage? usage = null;
        if (root.TryGetProperty("usageMetadata", out var usageMetadata) && usageMetadata.ValueKind == JsonValueKind.Object)
        {
            usage = new AiUsage(
                ReadNullableInt(usageMetadata, "promptTokenCount"),
                ReadNullableInt(usageMetadata, "candidatesTokenCount"),
                ReadNullableInt(usageMetadata, "totalTokenCount"));
        }

        return new AiResponse(text, modelId, finishReason, usage);
    }

    private static IEnumerable<string> ReadCandidateText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            yield break;

        var candidate = candidates.EnumerateArray().FirstOrDefault();
        if (candidate.ValueKind != JsonValueKind.Object ||
            !candidate.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var part in parts.EnumerateArray())
        {
            if (TryReadString(part, "text", out var text))
                yield return text;
        }
    }

    private static bool HasFinishReason(JsonElement root) =>
        root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array &&
        candidates.EnumerateArray().Any(candidate => TryReadString(candidate, "finishReason", out _));

    private static string? ReadFirstCandidateString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            var first = candidates.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && TryReadString(first, propertyName, out var value))
                return value;
        }

        return null;
    }

    private static bool SupportsGenerateContent(JsonElement model) =>
        model.TryGetProperty("supportedGenerationMethods", out var methods) && methods.ValueKind == JsonValueKind.Array &&
        methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String &&
            string.Equals(method.GetString(), "generateContent", StringComparison.Ordinal));

    private static string NormalizeModelId(string modelId)
    {
        var value = modelId?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new AiGatewayException(AiErrorCode.Configuration, "Gemini model ID is required.");

        var suffix = value.StartsWith("models/", StringComparison.Ordinal) ? value["models/".Length..] : value;
        if (suffix.Length is 0 or > 200 || suffix.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '+'))
            throw new AiGatewayException(AiErrorCode.Configuration, "Gemini model ID is invalid.");

        return "models/" + suffix;
    }

    private static Uri AddPageToken(Uri endpoint, string token)
    {
        var builder = new UriBuilder(endpoint);
        var query = builder.Query.TrimStart('?');
        builder.Query = $"{query}&pageToken={Uri.EscapeDataString(token)}";
        return builder.Uri;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static int? ReadNullableInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : null;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var error = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => AiErrorCode.Authentication,
            HttpStatusCode.Forbidden => AiErrorCode.Authorization,
            HttpStatusCode.TooManyRequests => AiErrorCode.RateLimit,
            _ => AiErrorCode.ProviderFailure
        };
        throw new AiGatewayException(error, $"Gemini returned HTTP {(int)response.StatusCode}.");
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
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Gemini returned invalid JSON.");
        }
    }

    private static JsonDocument ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Gemini stream contained invalid JSON.");
        }
    }

    private static async IAsyncEnumerable<string> ReadEventDataAsync(
        System.IO.Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return string.Join("\n", dataLines);
                    dataLines.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
                dataLines.Add(line[5..].TrimStart());
        }

        if (dataLines.Count > 0)
            yield return string.Join("\n", dataLines);
    }

    private static bool SameOrigin(Uri requested, Uri actual) =>
        string.Equals(requested.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requested.Host, actual.Host, StringComparison.OrdinalIgnoreCase) && requested.Port == actual.Port;
}
