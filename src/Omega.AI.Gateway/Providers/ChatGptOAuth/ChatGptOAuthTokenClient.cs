using System.Net;
using System.Text.Json;
using Omega.AI.Gateway.Errors;

namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public sealed class ChatGptOAuthTokenClient
{
    private readonly HttpClient _httpClient;
    private readonly ChatGptOAuthOptions _options;

    public ChatGptOAuthTokenClient(HttpClient httpClient, ChatGptOAuthOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public Task<ChatGptTokenSet> ExchangeCodeAsync(
        string code, string clientId, string verifier, Uri redirectUri, CancellationToken cancellationToken = default) =>
        SendAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["resource"] = _options.Resource
        }, requireScopeAndIdentity: true, cancellationToken);

    public Task<ChatGptTokenSet> RefreshAsync(
        ChatGptOAuthCredential credential, CancellationToken cancellationToken = default) =>
        SendAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = credential.ClientId,
            ["refresh_token"] = credential.RefreshToken,
            ["resource"] = _options.Resource
        }, requireScopeAndIdentity: false, cancellationToken);

    private async Task<ChatGptTokenSet> SendAsync(Dictionary<string, string> fields, bool requireScopeAndIdentity, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? request.RequestUri;
            if ((int)response.StatusCode is >= 300 and < 400 || finalUri is null ||
                finalUri.Scheme != Uri.UriSchemeHttps || finalUri.Host != "auth.openai.com" || finalUri.Port != 443)
            {
                throw new AiGatewayException(AiErrorCode.Authorization, "OpenAI token endpoint redirected or changed origin.");
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest => AiErrorCode.Authentication,
                    HttpStatusCode.Forbidden => AiErrorCode.Authorization,
                    HttpStatusCode.TooManyRequests => AiErrorCode.RateLimit,
                    _ => AiErrorCode.ProviderFailure
                };
                throw new AiGatewayException(code, "ChatGPT token endpoint rejected the request.");
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(CancellationToken.None), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var root = json.RootElement;
            var accessToken = Required(root, "access_token");
            var refreshToken = Optional(root, "refresh_token");
            var idToken = Optional(root, "id_token");
            var scopeText = Optional(root, "scope");
            var tokenType = Optional(root, "token_type") ?? "Bearer";
            if (!string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                throw new AiGatewayException(AiErrorCode.MalformedResponse, "ChatGPT token response used an unsupported token type.");
            }

            if (requireScopeAndIdentity && (refreshToken is null || idToken is null || scopeText is null))
            {
                throw new AiGatewayException(AiErrorCode.MalformedResponse, "ChatGPT sign-in token response was incomplete.");
            }

            var expiresIn = root.GetProperty("expires_in").GetInt32();
            if (expiresIn <= 0)
            {
                throw new AiGatewayException(AiErrorCode.MalformedResponse, "ChatGPT token expiry was invalid.");
            }

            return new ChatGptTokenSet(accessToken, refreshToken, idToken, tokenType,
                expiresIn,
                scopeText?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal));
        }
        catch (AiGatewayException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "ChatGPT token response was malformed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AiGatewayException(AiErrorCode.NetworkFailure, "ChatGPT token exchange failed.");
        }
    }

    private static string Required(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new AiGatewayException(AiErrorCode.MalformedResponse, "ChatGPT token response was incomplete.");
        }

        return value.GetString()!;
    }

    private static string? Optional(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed record ChatGptTokenSet(string AccessToken, string? RefreshToken, string? IdToken, string TokenType, int ExpiresIn, IReadOnlySet<string>? Scopes)
{
    public override string ToString() => $"{nameof(ChatGptTokenSet)} {{ TokenType = {TokenType}, ExpiresIn = {ExpiresIn}, Tokens = [REDACTED] }}";
}
