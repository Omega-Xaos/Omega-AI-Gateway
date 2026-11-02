using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.IdentityModel.Tokens;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Providers.ChatGptOAuth;
using Xunit;

namespace Omega.AI.Gateway.Tests.Providers;

public sealed class ChatGptOAuthAuthorizationTests
{
    [Fact]
    public void Pkce_challenge_uses_sha256_base64url_without_padding()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", ChatGptOAuthAuthorizationService.PkceChallenge(verifier));
    }

    [Fact]
    public void OAuth_options_reject_nonofficial_token_endpoint_port()
    {
        var options = new ChatGptOAuthOptions
        {
            TokenEndpoint = new Uri("https://auth.openai.com:8443/api/accounts/oauth/token")
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public async Task Begin_uses_system_browser_contract_and_exact_loopback_callback()
    {
        var browser = new RecordingBrowser();
        var service = Create(browser, new CountingHandler());

        var attempt = await service.BeginSignInAsync("profile-1", "urn:uuid:host-one");

        var query = HttpUtility.ParseQueryString(attempt.AuthorizationUri.Query);
        Assert.Equal("https", attempt.AuthorizationUri.Scheme);
        Assert.Equal("auth.openai.com", attempt.AuthorizationUri.Host);
        Assert.Equal("http", attempt.CallbackUri.Scheme);
        Assert.Equal("127.0.0.1", attempt.CallbackUri.Host);
        Assert.Equal("/auth/callback", attempt.CallbackUri.AbsolutePath);
        Assert.Equal(attempt.AuthorizationUri, browser.OpenedUri);
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("urn:uuid:host-one", query["ext_agent_host_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Contains("chatgpt.tokens.use.direct", query["scope"]);
    }

    [Fact]
    public async Task Wrong_callback_state_fails_before_token_exchange()
    {
        var handler = new CountingHandler();
        var service = Create(new RecordingBrowser(), handler);
        var attempt = await service.BeginSignInAsync("profile-1", "urn:uuid:host-one");

        using var http = new HttpClient();
        var response = await http.GetAsync(new Uri(attempt.CallbackUri + "?code=not-used&state=forged&client_id=oaiapp_forged"));
        var error = await Assert.ThrowsAsync<AiGatewayException>(() => service.WaitForSignInAsync("profile-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AiErrorCode.Authorization, error.Code);
        Assert.Equal(0, handler.CallCount);
        var secondWait = await Assert.ThrowsAsync<AiGatewayException>(() => service.WaitForSignInAsync("profile-1"));
        Assert.Equal(AiErrorCode.Configuration, secondWait.Code);
    }

    [Fact]
    public async Task Only_one_waiter_can_claim_an_authorization_attempt()
    {
        var handler = new CountingHandler();
        var service = Create(new RecordingBrowser(), handler);
        await using (service.ConfigureAwait(false))
        {
            var attempt = await service.BeginSignInAsync("profile-1", "urn:uuid:host-one");
            var wait = service.WaitForSignInAsync("profile-1");
            var second = await Assert.ThrowsAsync<AiGatewayException>(() => service.WaitForSignInAsync("profile-1"));
            using var http = new HttpClient();
            await http.GetAsync(new Uri(attempt.CallbackUri + "?code=unused&state=bad&client_id=oaiapp_test"));

            Assert.Equal(AiErrorCode.Configuration, second.Code);
            var first = await Assert.ThrowsAsync<AiGatewayException>(() => wait);
            Assert.Equal(AiErrorCode.Authorization, first.Code);
            Assert.Equal(0, handler.CallCount);
        }
    }

    [Fact]
    public async Task Expired_attempt_is_cleaned_up_and_a_new_sign_in_can_start()
    {
        var handler = new CountingHandler();
        var options = new ChatGptOAuthOptions { SignInTimeout = TimeSpan.FromMilliseconds(40) };
        var browser = new RecordingBrowser();
        var service = Create(browser, handler, options);
        await using (service.ConfigureAwait(false))
        {
            await service.BeginSignInAsync("profile-1", "urn:uuid:host-one");
            var expired = await Assert.ThrowsAsync<AiGatewayException>(() => service.WaitForSignInAsync("profile-1"));
            Assert.Equal(AiErrorCode.Timeout, expired.Code);

            var next = await service.BeginSignInAsync("profile-1", "urn:uuid:host-one");
            Assert.Equal("/auth/callback", next.CallbackUri.AbsolutePath);
        }
    }

    [Fact]
    public async Task Access_denied_with_matching_state_does_not_exchange_code()
    {
        var handler = new CountingHandler();
        var service = Create(new RecordingBrowser(), handler);
        await using (service.ConfigureAwait(false))
        {
            var attempt = await service.BeginSignInAsync("profile-1", "urn:uuid:host-one");
            var state = HttpUtility.ParseQueryString(attempt.AuthorizationUri.Query)["state"];
            using var http = new HttpClient();
            await http.GetAsync(new Uri(attempt.CallbackUri + "?error=access_denied&state=" + Uri.EscapeDataString(state!)));

            var error = await Assert.ThrowsAsync<AiGatewayException>(() => service.WaitForSignInAsync("profile-1"));
            Assert.Equal(AiErrorCode.Authorization, error.Code);
            Assert.Contains("declined", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.CallCount);
        }
    }

    [Fact]
    public async Task Reauthorization_rejects_a_different_issued_client_id()
    {
        var handler = new CountingHandler();
        var saved = new ChatGptOAuthCredential("oaiapp_saved", "urn:uuid:host-one", "subject-1", null,
            "access", "refresh", "id", DateTimeOffset.UtcNow.AddHours(1), new HashSet<string> { "chatgpt.tokens.use.direct" });
        var store = new MemoryCredentialStore(saved);
        var service = Create(new RecordingBrowser(), handler, credentialStore: store);
        await using (service.ConfigureAwait(false))
        {
            var attempt = await service.BeginSignInAsync("profile-1", "urn:uuid:host-one");
            var state = HttpUtility.ParseQueryString(attempt.AuthorizationUri.Query)["state"];
            using var http = new HttpClient();
            await http.GetAsync(new Uri(attempt.CallbackUri + "?code=unused&state=" + Uri.EscapeDataString(state!) + "&client_id=oaiapp_other"));

            var error = await Assert.ThrowsAsync<AiGatewayException>(() => service.WaitForSignInAsync("profile-1"));
            Assert.Equal(AiErrorCode.Authorization, error.Code);
            Assert.Equal(0, handler.CallCount);
            Assert.Equal(saved, store.Credential);
        }
    }

    [Fact]
    public async Task Successful_sign_in_binds_pkce_and_saves_only_validated_identity()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "auth-key" };
        var options = new ChatGptOAuthOptions();
        var browser = new RecordingBrowser();
        var store = new MemoryCredentialStore();
        var tokenHandler = new FormRecordingHandler(request =>
        {
            var idToken = MakeIdToken(key, "oaiapp_issued", HttpUtility.ParseQueryString(browser.OpenedUri!.Query)["nonce"]!);
            return Json($"{{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"id_token\":\"{idToken}\",\"token_type\":\"Bearer\",\"expires_in\":3600,\"scope\":\"openid email offline_access resource.invoke chatgpt.tokens.use.direct\"}}");
        });
        var service = new ChatGptOAuthAuthorizationService(options, store, browser,
            new ChatGptOAuthTokenClient(new HttpClient(tokenHandler), options),
            new ChatGptIdTokenValidator(new HttpClient(new FormRecordingHandler(_ => Json(Jwks(rsa)))), options));
        await using (service.ConfigureAwait(false))
        {
            var attempt = await service.BeginSignInAsync("profile-1", "urn:uuid:test-host");
            var authQuery = HttpUtility.ParseQueryString(attempt.AuthorizationUri.Query);
            using var local = new HttpClient();
            using var callback = await local.GetAsync(new Uri(attempt.CallbackUri + "?code=one-time-code&state=" + Uri.EscapeDataString(authQuery["state"]!) + "&client_id=oaiapp_issued"));

            var credential = await service.WaitForSignInAsync("profile-1");

            var submitted = HttpUtility.ParseQueryString(tokenHandler.Body!);
            Assert.Equal("one-time-code", submitted["code"]);
            Assert.Equal("oaiapp_issued", submitted["client_id"]);
            Assert.Equal(authQuery["code_challenge"], PkceChallenge(submitted["code_verifier"]!));
            Assert.Equal("subject-1", credential.Subject);
            Assert.Equal("urn:uuid:test-host", credential.HostId);
            Assert.Equal(credential, store.Credential);
            Assert.Contains("chatgpt.tokens.use.direct", credential.Scopes);
        }
    }

    private static ChatGptOAuthAuthorizationService Create(
        RecordingBrowser browser,
        CountingHandler tokenHandler,
        ChatGptOAuthOptions? suppliedOptions = null,
        MemoryCredentialStore? credentialStore = null)
    {
        var options = suppliedOptions ?? new ChatGptOAuthOptions();
        var credentials = credentialStore ?? new MemoryCredentialStore();
        return new ChatGptOAuthAuthorizationService(options, credentials, browser,
            new ChatGptOAuthTokenClient(new HttpClient(tokenHandler), options),
            new ChatGptIdTokenValidator(new HttpClient(tokenHandler), options));
    }

    private static string MakeIdToken(RsaSecurityKey key, string audience, string nonce)
    {
        var token = new JwtSecurityToken("https://auth.openai.com", audience,
            [new Claim("sub", "subject-1"), new Claim("nonce", nonce), new Claim("email", "user@example.test")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Jwks(RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        return $$"""{"keys":[{"kty":"RSA","use":"sig","kid":"auth-key","alg":"RS256","n":"{{B64(p.Modulus!)}}","e":"{{B64(p.Exponent!)}}"}]}""";
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string PkceChallenge(string verifier) => B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingBrowser : IAuthorizationBrowser
    {
        public Uri? OpenedUri { get; private set; }
        public Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
        {
            OpenedUri = authorizationUri;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class MemoryCredentialStore : IChatGptOAuthCredentialStore
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public MemoryCredentialStore(ChatGptOAuthCredential? credential = null) => Credential = credential;
        public ChatGptOAuthCredential? Credential { get; private set; }
        public Task<ChatGptOAuthCredential?> GetAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult(Credential);
        public Task SaveAsync(string profileId, ChatGptOAuthCredential credential, CancellationToken cancellationToken = default)
        {
            Credential = credential;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async ValueTask<IAsyncDisposable> AcquireRefreshLockAsync(string profileId, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            return new GateLease(_gate);
        }
    }

    private sealed class GateLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FormRecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return callback(request);
        }
    }
}
