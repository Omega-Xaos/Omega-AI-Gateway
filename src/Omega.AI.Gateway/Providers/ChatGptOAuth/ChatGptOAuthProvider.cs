using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;

namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public sealed class ChatGptOAuthProvider : IAiProvider
{
    private const string RequiredScope = "chatgpt.tokens.use.direct";
    private readonly HttpClient _httpClient;
    private readonly ChatGptOAuthOptions _options;
    private readonly IChatGptOAuthCredentialStore _credentials;
    private readonly ChatGptOAuthTokenClient _tokens;
    private readonly ChatGptIdTokenValidator _idTokens;

    public ChatGptOAuthProvider(
        HttpClient httpClient,
        ChatGptOAuthOptions options,
        IChatGptOAuthCredentialStore credentials,
        ChatGptOAuthTokenClient tokens,
        ChatGptIdTokenValidator idTokens)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _idTokens = idTokens ?? throw new ArgumentNullException(nameof(idTokens));
        _options.Validate();
    }

    public string ProviderKind => "chatgpt-oauth";

    public async Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(AiProfile profile, CancellationToken cancellationToken = default)
    {
        var credential = await GetValidCredentialAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        using var request = CreateApiRequest(HttpMethod.Get, "models", credential.AccessToken);
        using var response = await SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureApiSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "ChatGPT model catalog response was malformed.");
        }

        return models.EnumerateArray()
            .Where(model => model.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list" &&
                            model.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String)
            .Select(model => new AiModelInfo(model.GetProperty("slug").GetString()!,
                model.TryGetProperty("display_name", out var name) ? name.GetString() : null))
            .ToArray();
    }

    public async Task<AiConnectionTest> TestConnectionAsync(AiProfile profile, CancellationToken cancellationToken = default)
    {
        var models = await GetModelsAsync(profile, cancellationToken).ConfigureAwait(false);
        return new AiConnectionTest(true, "ChatGPT account model catalog is reachable.", models.FirstOrDefault()?.Id);
    }

    public async Task<AiResponse> SendAsync(AiProfile profile, AiRequest request, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        AiResponse? completed = null;
        await foreach (var item in StreamAsync(profile, request, cancellationToken).ConfigureAwait(false))
        {
            if (item.Kind == AiStreamEventKind.TextDelta)
            {
                text.Append(item.Text);
            }
            else if (item.Kind == AiStreamEventKind.Completed)
            {
                completed = item.Response;
            }
        }

        return completed ?? throw new AiGatewayException(AiErrorCode.MalformedResponse, "ChatGPT stream ended before response.completed.");
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiProfile profile,
        AiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credential = await GetValidCredentialAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        var model = request.ModelId ?? profile.ModelId;
        var payload = ResponsesProtocol.BuildRequest(profile, request, model);
        using var httpRequest = CreateApiRequest(HttpMethod.Post, "responses", credential.AccessToken);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = JsonContent.Create(payload, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var response = await SendApiAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureApiSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        var completed = false;
        await foreach (var data in OpenAiCompatible.SseReader.ReadDataAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            using var json = ParseEvent(data);
            var eventType = json.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
            if (eventType == "response.output_text.delta" && ResponsesProtocol.ReadDelta(json.RootElement) is { } delta)
            {
                text.Append(delta);
                yield return new AiStreamEvent(AiStreamEventKind.TextDelta, delta);
            }
            else if (eventType == "response.completed")
            {
                completed = true;
                var result = ResponsesProtocol.ReadCompleted(json.RootElement, model, text.ToString());
                yield return new AiStreamEvent(AiStreamEventKind.Completed, Response: result);
                yield break;
            }
            else if (eventType is "response.failed" or "response.incomplete" or "error")
            {
                throw ResponsesProtocol.ReadFailure(json.RootElement);
            }
        }

        if (!completed)
        {
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "ChatGPT stream ended before response.completed.");
        }
    }

    private async Task<ChatGptOAuthCredential> GetValidCredentialAsync(string profileId, CancellationToken cancellationToken)
    {
        var credential = await _credentials.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT account credential is missing.");
        }

        if (credential.PendingIdToken is not null)
        {
            credential = await CompletePendingIdentityAsync(profileId, cancellationToken).ConfigureAwait(false);
        }

        EnsurePlanScope(credential);

        if (credential.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(_options.RefreshSkew))
        {
            return credential;
        }

        ChatGptOAuthCredential replacement;
        await using (await _credentials.AcquireRefreshLockAsync(profileId, cancellationToken).ConfigureAwait(false))
        {
            credential = await _credentials.GetAsync(profileId, cancellationToken).ConfigureAwait(false)
                ?? throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT account credential is missing.");
            credential = await CompletePendingIdentityUnderLockAsync(profileId, credential, cancellationToken).ConfigureAwait(false);
            EnsurePlanScope(credential);
            if (credential.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(_options.RefreshSkew))
            {
                return credential;
            }

            var refreshed = await _tokens.RefreshAsync(credential, cancellationToken).ConfigureAwait(false);
            replacement = credential with
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken ?? credential.RefreshToken,
                IdToken = refreshed.IdToken ?? credential.IdToken,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn),
                Scopes = refreshed.Scopes ?? credential.Scopes,
                PendingIdToken = refreshed.IdToken
            };
            await _credentials.SaveAsync(profileId, replacement, CancellationToken.None).ConfigureAwait(false);
        }

        var validatedReplacement = await CompletePendingIdentityAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        EnsurePlanScope(validatedReplacement);
        return validatedReplacement;
    }

    private static void EnsurePlanScope(ChatGptOAuthCredential credential)
    {
        if (!credential.Scopes.Contains(RequiredScope))
        {
            throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT account is not authorized for plan inference.");
        }
    }

    private async Task<ChatGptOAuthCredential> CompletePendingIdentityAsync(
        string profileId, CancellationToken cancellationToken)
    {
        await using var credentialLock = await _credentials.AcquireRefreshLockAsync(profileId, cancellationToken).ConfigureAwait(false);
        var latest = await _credentials.GetAsync(profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT account credential is missing.");
        return await CompletePendingIdentityUnderLockAsync(profileId, latest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChatGptOAuthCredential> CompletePendingIdentityUnderLockAsync(
        string profileId, ChatGptOAuthCredential credential, CancellationToken cancellationToken)
    {
        if (credential.PendingIdToken is null)
        {
            return credential;
        }

        var identity = await _idTokens.ValidateAsync(credential.PendingIdToken, credential.ClientId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.Equals(identity.Subject, credential.Subject, StringComparison.Ordinal))
        {
            throw new AiGatewayException(AiErrorCode.Authorization, "Refreshed ChatGPT identity did not match the selected account.");
        }

        var validated = credential with
        {
            Subject = identity.Subject,
            Email = identity.Email ?? credential.Email,
            IdToken = credential.PendingIdToken,
            PendingIdToken = null
        };
        await _credentials.SaveAsync(profileId, validated, CancellationToken.None).ConfigureAwait(false);
        return validated;
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, string path, string accessToken)
    {
        if (_options.ApiBaseUri.Scheme != Uri.UriSchemeHttps || _options.ApiBaseUri.Host != "api.openai.com")
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "ChatGPT OAuth API endpoint must use the official HTTPS host.");
        }

        var baseUri = new Uri(_options.ApiBaseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<HttpResponseMessage> SendApiAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var actual = response.RequestMessage?.RequestUri ?? request.RequestUri;
            if ((int)response.StatusCode is >= 300 and < 400 || actual is null ||
                actual.Scheme != Uri.UriSchemeHttps || actual.Host != "api.openai.com" || actual.Port != 443)
            {
                response.Dispose();
                throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT API redirected the request away from the official host.");
            }

            return response;
        }
        catch (AiGatewayException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { throw new AiGatewayException(AiErrorCode.NetworkFailure, "ChatGPT API request failed."); }
    }

    private static async Task EnsureApiSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => AiErrorCode.Authentication,
            HttpStatusCode.Forbidden => AiErrorCode.Authorization,
            HttpStatusCode.TooManyRequests => AiErrorCode.RateLimit,
            _ => AiErrorCode.ProviderFailure
        };
        throw new AiGatewayException(code, $"ChatGPT API returned HTTP {(int)response.StatusCode}.");
    }

    private static JsonDocument ParseEvent(string data)
    {
        try { return JsonDocument.Parse(data); }
        catch (JsonException) { throw new AiGatewayException(AiErrorCode.MalformedResponse, "ChatGPT stream contained invalid event data."); }
    }
}
