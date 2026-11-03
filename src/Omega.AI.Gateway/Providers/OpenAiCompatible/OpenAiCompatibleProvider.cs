using System.Net.Http.Json;
using System.Text.Json;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Security;

namespace Omega.AI.Gateway.Providers.OpenAiCompatible;

public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly IAiSecretResolver _secrets;

    public OpenAiCompatibleProvider(HttpClient httpClient, IAiSecretResolver secrets)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public string ProviderKind => "openai-compatible";

    public async Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(AiProfile profile, CancellationToken cancellationToken = default) =>
        (await GetModelsCoreAsync(profile, cancellationToken).ConfigureAwait(false)).Models;

    private async Task<(IReadOnlyList<AiModelInfo> Models, bool CatalogAvailable)> GetModelsCoreAsync(
        AiProfile profile, CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, OpenAiEndpointPolicy.Build(profile, "models"), profile, null, cancellationToken).ConfigureAwait(false);
        using var response = await SendHttpAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
        {
            return ([new AiModelInfo(profile.ModelId)], false);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Provider model response was malformed.");
        }

        var models = data.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(item => new AiModelInfo(item.GetProperty("id").GetString()!))
            .ToArray();
        return (models, true);
    }

    public async Task<AiConnectionTest> TestConnectionAsync(AiProfile profile, CancellationToken cancellationToken = default)
    {
        var (models, catalogAvailable) = await GetModelsCoreAsync(profile, cancellationToken).ConfigureAwait(false);
        return catalogAvailable
            ? new AiConnectionTest(true, "Model catalog is reachable.", models.FirstOrDefault()?.Id ?? profile.ModelId)
            : new AiConnectionTest(false, "Provider does not expose model discovery; connection was not verified without an inference request.", profile.ModelId);
    }

    public async Task<AiResponse> SendAsync(AiProfile profile, AiRequest request, CancellationToken cancellationToken = default)
    {
        var model = request.ModelId ?? profile.ModelId;
        var payload = OpenAiCompatibleProtocol.BuildRequest(profile, request, model, stream: false);
        using var httpRequest = await CreateRequestAsync(HttpMethod.Post, OpenAiEndpointPolicy.Build(profile, "chat/completions"), profile, payload, cancellationToken).ConfigureAwait(false);
        using var response = await SendHttpAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return OpenAiCompatibleProtocol.ParseResponse(document.RootElement, model, response.Headers);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiProfile profile,
        AiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = request.ModelId ?? profile.ModelId;
        var payload = OpenAiCompatibleProtocol.BuildRequest(profile, request, model, stream: true);
        using var httpRequest = await CreateRequestAsync(HttpMethod.Post, OpenAiEndpointPolicy.Build(profile, "chat/completions"), profile, payload, cancellationToken).ConfigureAwait(false);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await SendHttpAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var completed = false;
        await foreach (var data in SseReader.ReadDataAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (data == "[DONE]")
            {
                completed = true;
                break;
            }

            using var document = ParseJson(data);
            if (OpenAiCompatibleProtocol.TryParseDelta(document.RootElement, out var delta))
            {
                yield return new AiStreamEvent(AiStreamEventKind.TextDelta, delta);
            }
        }

        if (!completed)
        {
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Provider stream ended before its completion marker.");
        }

        yield return new AiStreamEvent(AiStreamEventKind.Completed);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method, Uri uri, AiProfile profile, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: OpenAiCompatibleProtocol.JsonOptions);
        }

        if (profile.AuthMode == AiAuthMode.SecretReference)
        {
            if (string.IsNullOrWhiteSpace(profile.SecretReference))
            {
                request.Dispose();
                throw new AiGatewayException(AiErrorCode.Configuration, "Provider secret reference is missing.");
            }

            var headerName = profile.SecretHeaderName ?? "Authorization";
            var headerPrefix = profile.SecretHeaderPrefix ??
                (string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer " : string.Empty);
            if (!IsValidSecretHeader(headerName, headerPrefix))
            {
                request.Dispose();
                throw new AiGatewayException(AiErrorCode.Configuration, "Provider API-key header configuration is invalid.");
            }

            var secret = await _secrets.ResolveAsync(profile.SecretReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(secret) || secret.Length > 8_192 || secret.Any(char.IsControl))
            {
                request.Dispose();
                throw new AiGatewayException(AiErrorCode.Authentication, "Provider credential is unavailable.");
            }

            if (!request.Headers.TryAddWithoutValidation(headerName, headerPrefix + secret))
            {
                request.Dispose();
                throw new AiGatewayException(AiErrorCode.Configuration, "Provider API-key header could not be added.");
            }
        }
        else if (profile.AuthMode != AiAuthMode.None)
        {
            request.Dispose();
            throw new AiGatewayException(AiErrorCode.UnsupportedCapability, "This provider does not support the configured authentication mode.");
        }

        request.Headers.Accept.ParseAdd(method == HttpMethod.Post ? "application/json" : "application/json");
        return request;
    }

    private static bool IsValidSecretHeader(string name, string prefix)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
            prefix.Length > 64 || prefix.Any(char.IsControl))
        {
            return false;
        }

        return name.ToLowerInvariant() is not "host" and not "content-length" and not "transfer-encoding" and
            not "connection" and not "cookie" and not "proxy-authorization" and not "upgrade";
    }

    private async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new AiGatewayException(AiErrorCode.Authorization, "Provider redirects are disabled for credential-bearing requests.");
            }
            if (response.RequestMessage?.RequestUri is { } actual && !SameOrigin(request.RequestUri!, actual))
            {
                response.Dispose();
                throw new AiGatewayException(AiErrorCode.Authorization, "Provider redirected the credential-bearing request to another origin.");
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
            throw new AiGatewayException(AiErrorCode.NetworkFailure, "Provider network request failed.");
        }
    }

    private static bool SameOrigin(Uri requested, Uri actual) =>
        string.Equals(requested.Scheme, actual.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requested.Host, actual.Host, StringComparison.OrdinalIgnoreCase) && requested.Port == actual.Port;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => AiErrorCode.Authentication,
            System.Net.HttpStatusCode.Forbidden => AiErrorCode.Authorization,
            System.Net.HttpStatusCode.TooManyRequests => AiErrorCode.RateLimit,
            _ => AiErrorCode.ProviderFailure
        };
        throw new AiGatewayException(code, $"Provider returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
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
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Provider returned invalid JSON.");
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
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "Provider stream contained invalid JSON.");
        }
    }
}
