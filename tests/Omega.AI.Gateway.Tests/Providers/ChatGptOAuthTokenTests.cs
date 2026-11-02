using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Providers.ChatGptOAuth;
using Xunit;

namespace Omega.AI.Gateway.Tests.Providers;

public sealed class ChatGptOAuthTokenTests
{
    [Fact]
    public void Credential_and_token_diagnostics_redact_token_material()
    {
        var credential = new ChatGptOAuthCredential("client", "host", "subject", "mail", "access-secret", "refresh-secret", "id-secret",
            DateTimeOffset.UtcNow, new HashSet<string>());
        var tokens = new ChatGptTokenSet("access-secret", "refresh-secret", "id-secret", "Bearer", 10, new HashSet<string>());

        Assert.DoesNotContain("access-secret", credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret", credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("id-secret", tokens.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validator_accepts_signature_issuer_audience_expiry_and_nonce()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var handler = new StubHandler(_ => Json(Jwks(rsa)));
        var validator = new ChatGptIdTokenValidator(new HttpClient(handler), new ChatGptOAuthOptions());
        var token = CreateToken(key, "https://auth.openai.com", "issued-client", "nonce-1", DateTime.UtcNow.AddMinutes(3));

        var identity = await validator.ValidateAsync(token, "issued-client", "nonce-1");

        Assert.Equal("subject-1", identity.Subject);
        Assert.Equal("user@example.test", identity.Email);
    }

    [Fact]
    public async Task Validator_rejects_a_signature_from_an_unpublished_key()
    {
        using var publishedRsa = RSA.Create(2048);
        using var untrustedRsa = RSA.Create(2048);
        var untrustedKey = new RsaSecurityKey(untrustedRsa) { KeyId = "test-key" };
        var handler = new StubHandler(_ => Json(Jwks(publishedRsa)));
        var validator = new ChatGptIdTokenValidator(new HttpClient(handler), new ChatGptOAuthOptions());
        var token = CreateToken(untrustedKey, "https://auth.openai.com", "issued-client", "nonce-1", DateTime.UtcNow.AddMinutes(3));

        await Assert.ThrowsAsync<AiGatewayException>(() => validator.ValidateAsync(token, "issued-client", "nonce-1"));
    }

    [Theory]
    [InlineData("wrong-issuer", "issued-client", "nonce-1", 3, false)]
    [InlineData("https://auth.openai.com", "wrong-client", "nonce-1", 3, false)]
    [InlineData("https://auth.openai.com", "issued-client", "wrong-nonce", 3, false)]
    [InlineData("https://auth.openai.com", "issued-client", "nonce-1", -3, false)]
    public async Task Validator_rejects_identity_token_claim_mismatches(
        string issuer, string audience, string nonce, int expiryMinutes, bool _)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var validator = new ChatGptIdTokenValidator(new HttpClient(new StubHandler(_ => Json(Jwks(rsa)))), new ChatGptOAuthOptions());
        var token = CreateToken(key, issuer, audience, "nonce-1", DateTime.UtcNow.AddMinutes(expiryMinutes));

        await Assert.ThrowsAsync<AiGatewayException>(() => validator.ValidateAsync(token, "issued-client", nonce));
    }

    [Fact]
    public async Task Token_client_exchanges_code_with_pkce_and_exact_loopback_redirect()
    {
        string? form = null;
        var handler = new StubHandler(async request =>
        {
            form = await request.Content!.ReadAsStringAsync();
            return Json("""{"access_token":"a","refresh_token":"r","id_token":"i","token_type":"Bearer","expires_in":3600,"scope":"chatgpt.tokens.use.direct resource.invoke"}""");
        });
        var client = new ChatGptOAuthTokenClient(new HttpClient(handler), new ChatGptOAuthOptions());
        var callback = new Uri("http://127.0.0.1:12345/auth/callback");

        var tokens = await client.ExchangeCodeAsync("code", "oaiapp_issued", "pkce-verifier", callback);

        Assert.Contains("code_verifier=pkce-verifier", form);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A12345%2Fauth%2Fcallback", form);
        Assert.Equal("a", tokens.AccessToken);
        Assert.Contains("chatgpt.tokens.use.direct", tokens.Scopes!);
    }

    private static string CreateToken(RsaSecurityKey key, string issuer, string audience, string nonce, DateTime expires)
    {
        var claims = new[]
        {
            new Claim("sub", "subject-1"), new Claim("nonce", nonce), new Claim("email", "user@example.test")
        };
        var token = new JwtSecurityToken(issuer, audience, claims, DateTime.UtcNow.AddMinutes(-10), expires,
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Jwks(RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        return $$"""{"keys":[{"kty":"RSA","use":"sig","kid":"test-key","alg":"RS256","n":"{{B64(p.Modulus!)}}","e":"{{B64(p.Exponent!)}}"}]}""";
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : this(request => Task.FromResult(callback(request))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
