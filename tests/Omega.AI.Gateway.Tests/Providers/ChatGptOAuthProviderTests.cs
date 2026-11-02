using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Providers.ChatGptOAuth;
using Xunit;

namespace Omega.AI.Gateway.Tests.Providers;

public sealed class ChatGptOAuthProviderTests
{
    [Fact]
    public async Task Model_discovery_filters_to_displayable_catalog_entries()
    {
        var handler = new RecordingHandler(_ => Json("""{"models":[{"slug":"m1","display_name":"Model 1","visibility":"list"},{"slug":"hidden","visibility":"internal"}]}"""));
        var provider = Create(handler, Credential());

        var models = await provider.GetModelsAsync(Profile());

        Assert.Equal("m1", Assert.Single(models).Id);
        Assert.Equal("Model 1", models[0].DisplayName);
        Assert.Equal("Bearer oauth-token", handler.Request!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Responses_request_is_nonstored_stream_and_maps_instructions()
    {
        var handler = new RecordingHandler(_ => EventStream(
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"model\":\"m1\",\"usage\":{\"input_tokens\":2,\"output_tokens\":3}}}\n\n"));
        var provider = Create(handler, Credential());
        var request = new AiRequest("profile-1", [
            new AiMessage(AiMessageRole.System, "Be concise."),
            new AiMessage(AiMessageRole.User, "Hello")], "m1");

        var response = await provider.SendAsync(Profile(), request);

        Assert.Equal("hello", response.Text);
        Assert.Equal("resp-1", response.ProviderRequestId);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.False(payload.RootElement.GetProperty("store").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("Be concise.", payload.RootElement.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task Stream_fails_if_provider_fails_after_emitting_text()
    {
        var handler = new RecordingHandler(_ => EventStream(
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n" +
            "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\"}}}\n\n"));
        var provider = Create(handler, Credential());
        var seen = new List<AiStreamEvent>();

        var error = await Assert.ThrowsAsync<AiGatewayException>(async () =>
        {
            await foreach (var item in provider.StreamAsync(Profile(), Request())) seen.Add(item);
        });

        Assert.Single(seen);
        Assert.Equal(AiErrorCode.RateLimit, error.Code);
    }

    [Fact]
    public async Task Stream_without_response_completed_is_not_success()
    {
        var handler = new RecordingHandler(_ => EventStream("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"));
        var provider = Create(handler, Credential());

        var error = await Assert.ThrowsAsync<AiGatewayException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(Profile(), Request())) { }
        });

        Assert.Equal(AiErrorCode.MalformedResponse, error.Code);
    }

    [Fact]
    public async Task Incomplete_response_after_delta_never_emits_completed()
    {
        var handler = new RecordingHandler(_ => EventStream(
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n" +
            "data: {\"type\":\"response.incomplete\",\"response\":{\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n"));
        var provider = Create(handler, Credential());
        var seen = new List<AiStreamEvent>();

        await Assert.ThrowsAsync<AiGatewayException>(async () =>
        {
            await foreach (var item in provider.StreamAsync(Profile(), Request())) seen.Add(item);
        });

        Assert.Single(seen);
        Assert.DoesNotContain(seen, item => item.Kind == AiStreamEventKind.Completed);
    }

    [Fact]
    public async Task Json_response_format_is_rejected_before_network_request()
    {
        var handler = new RecordingHandler(_ => Json("{}"));
        var provider = Create(handler, Credential());

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.SendAsync(Profile(),
            new AiRequest("profile-1", [new AiMessage(AiMessageRole.User, "hello")], ResponseFormat: AiResponseFormat.Json)));

        Assert.Equal(AiErrorCode.UnsupportedCapability, error.Code);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Inference_requires_direct_use_scope()
    {
        var handler = new RecordingHandler(_ => Json("{}"));
        var provider = Create(handler, Credential(scopes: new HashSet<string>()));

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(Profile()));

        Assert.Equal(AiErrorCode.Authorization, error.Code);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Refresh_rotates_access_and_refresh_tokens_but_preserves_omitted_scope_and_id_token()
    {
        var oldCredential = Credential() with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var store = new MemoryCredentialStore(oldCredential);
        var tokenHandler = new RecordingHandler(_ => Json("""{"access_token":"fresh-access","refresh_token":"fresh-refresh","expires_in":3600,"token_type":"Bearer"}"""));
        var apiHandler = new RecordingHandler(_ => Json("""{"models":[]}"""));
        var provider = new ChatGptOAuthProvider(new HttpClient(apiHandler), new ChatGptOAuthOptions(), store,
            new ChatGptOAuthTokenClient(new HttpClient(tokenHandler), new ChatGptOAuthOptions()),
            new ChatGptIdTokenValidator(new HttpClient(new RecordingHandler(_ => Json("{}"))), new ChatGptOAuthOptions()));

        await provider.GetModelsAsync(Profile());

        Assert.Equal("Bearer fresh-access", apiHandler.Request!.Headers.Authorization!.ToString());
        Assert.Equal("fresh-refresh", store.Credential.RefreshToken);
        Assert.Equal("id-token", store.Credential.IdToken);
        Assert.Contains("chatgpt.tokens.use.direct", store.Credential.Scopes);
    }

    [Fact]
    public async Task Concurrent_requests_serialize_rotating_refresh_tokens()
    {
        var store = new MemoryCredentialStore(Credential() with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var tokenHandler = new RecordingHandler(_ => Json("""{"access_token":"fresh-access","refresh_token":"fresh-refresh","expires_in":3600,"token_type":"Bearer"}"""));
        var apiHandler = new RecordingHandler(_ => Json("""{"models":[]}"""));
        var provider = new ChatGptOAuthProvider(new HttpClient(apiHandler), new ChatGptOAuthOptions(), store,
            new ChatGptOAuthTokenClient(new HttpClient(tokenHandler), new ChatGptOAuthOptions()),
            new ChatGptIdTokenValidator(new HttpClient(new RecordingHandler(_ => Json("{}"))), new ChatGptOAuthOptions()));

        await Task.WhenAll(provider.GetModelsAsync(Profile()), provider.GetModelsAsync(Profile()));

        Assert.Equal(1, tokenHandler.CallCount);
        Assert.Equal(2, apiHandler.CallCount);
    }

    [Fact]
    public async Task Rotated_credential_is_saved_before_pending_identity_validation()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "refresh-key" };
        var idToken = CreateIdToken(key, "issued-client", "subject-1");
        var jwks = CreateJwks(rsa);
        var jwksCall = 0;
        var validatorHttp = new HttpClient(new RecordingHandler(_ => ++jwksCall == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(jwks)));
        var store = new MemoryCredentialStore(Credential() with
        {
            ClientId = "issued-client",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        var tokenHandler = new RecordingHandler(_ => Json($"{{\"access_token\":\"fresh-access\",\"refresh_token\":\"fresh-refresh\",\"id_token\":\"{idToken}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}"));
        var apiHandler = new RecordingHandler(_ => Json("""{"models":[]}"""));
        var provider = new ChatGptOAuthProvider(new HttpClient(apiHandler), new ChatGptOAuthOptions(), store,
            new ChatGptOAuthTokenClient(new HttpClient(tokenHandler), new ChatGptOAuthOptions()),
            new ChatGptIdTokenValidator(validatorHttp, new ChatGptOAuthOptions()));

        var first = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(Profile()));
        Assert.Equal(AiErrorCode.Authentication, first.Code);
        Assert.Equal("fresh-refresh", store.Credential.RefreshToken);
        Assert.Equal(idToken, store.Credential.PendingIdToken);
        Assert.Equal(0, apiHandler.CallCount);

        await provider.GetModelsAsync(Profile());

        Assert.Null(store.Credential.PendingIdToken);
        Assert.Equal("fresh-refresh", store.Credential.RefreshToken);
        Assert.Equal(1, tokenHandler.CallCount);
        Assert.Equal(1, apiHandler.CallCount);
    }

    private static ChatGptOAuthProvider Create(RecordingHandler handler, ChatGptOAuthCredential credential) =>
        new(new HttpClient(handler), new ChatGptOAuthOptions(), new MemoryCredentialStore(credential),
            new ChatGptOAuthTokenClient(new HttpClient(new RecordingHandler(_ => Json("{}"))), new ChatGptOAuthOptions()),
            new ChatGptIdTokenValidator(new HttpClient(new RecordingHandler(_ => Json("{}"))), new ChatGptOAuthOptions()));

    private static AiProfile Profile() => new("profile-1", "ChatGPT", "chatgpt-oauth",
        new Uri("https://api.openai.com/v1/"), "m1", AiAuthMode.ChatGptOAuth);

    private static AiRequest Request() => new("profile-1", [new AiMessage(AiMessageRole.User, "hello")]);

    private static ChatGptOAuthCredential Credential(IReadOnlySet<string>? scopes = null) => new(
        "oaiapp_test", "urn:uuid:test", "subject-1", null, "oauth-token", "refresh-token", "id-token",
        DateTimeOffset.UtcNow.AddHours(1), scopes ?? new HashSet<string> { "chatgpt.tokens.use.direct" });

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage EventStream(string events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
    };

    private static string CreateIdToken(RsaSecurityKey key, string audience, string subject)
    {
        var token = new JwtSecurityToken("https://auth.openai.com", audience,
            [new Claim("sub", subject), new Claim("email", "user@example.test")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateJwks(RSA rsa)
    {
        var parameters = rsa.ExportParameters(false);
        return $$"""{"keys":[{"kty":"RSA","use":"sig","kid":"refresh-key","alg":"RS256","n":"{{B64(parameters.Modulus!)}}","e":"{{B64(parameters.Exponent!)}}"}]}""";
    }

    private static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return callback(request);
        }
    }

    private sealed class MemoryCredentialStore(ChatGptOAuthCredential credential) : IChatGptOAuthCredentialStore
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        public ChatGptOAuthCredential Credential { get; private set; } = credential;
        public Task<ChatGptOAuthCredential?> GetAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult<ChatGptOAuthCredential?>(Credential);
        public Task SaveAsync(string profileId, ChatGptOAuthCredential value, CancellationToken cancellationToken = default)
        {
            Credential = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async ValueTask<IAsyncDisposable> AcquireRefreshLockAsync(string profileId, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            return new GateLease(_lock);
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
}
