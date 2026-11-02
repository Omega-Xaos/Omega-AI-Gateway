using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Omega.AI.Gateway.Errors;

namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public sealed record ChatGptAccountIdentity(string Subject, string? Email);

public sealed class ChatGptIdTokenValidator
{
    private readonly HttpClient _httpClient;
    private readonly ChatGptOAuthOptions _options;

    public ChatGptIdTokenValidator(HttpClient httpClient, ChatGptOAuthOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<ChatGptAccountIdentity> ValidateAsync(
        string idToken, string clientId, string? expectedNonce = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.JwksEndpoint);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? request.RequestUri;
            if ((int)response.StatusCode is >= 300 and < 400 || finalUri is null ||
                finalUri.Scheme != Uri.UriSchemeHttps || finalUri.Host != "auth.openai.com" || finalUri.Port != 443 || !response.IsSuccessStatusCode)
            {
                throw new AiGatewayException(AiErrorCode.Authentication, "OpenAI signing keys could not be loaded from the official endpoint.");
            }

            using var jwks = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
            var signingKeys = new JsonWebKeySet(jwks.RootElement.GetRawText()).GetSigningKeys();
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "https://auth.openai.com",
                ValidateAudience = true,
                ValidAudience = clientId,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = signingKeys,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out _);

            var subject = principal.FindFirst("sub")?.Value;
            var nonce = principal.FindFirst("nonce")?.Value;
            if (string.IsNullOrWhiteSpace(subject) ||
                expectedNonce is not null && !string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            {
                throw new AiGatewayException(AiErrorCode.Authentication, "ChatGPT identity token did not match the sign-in attempt.");
            }

            return new ChatGptAccountIdentity(subject, principal.FindFirst("email")?.Value);
        }
        catch (AiGatewayException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AiGatewayException(AiErrorCode.Authentication, "ChatGPT identity token validation failed.");
        }
    }
}
