using System.Net;
using System.Reflection;
using System.Text;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Providers.OpenAiCompatible;
using Omega.AI.Gateway.Security;

namespace Omega.AI.Gateway.Tests.Providers;

public sealed class OpenAiCompatibleCustomAuthTests
{
    [Fact]
    public async Task Configured_api_key_header_is_used_for_discovery_and_completion()
    {
        var headerName = typeof(AiProfile).GetProperty("SecretHeaderName");
        Assert.NotNull(headerName);
        var headerPrefix = typeof(AiProfile).GetProperty("SecretHeaderPrefix");
        Assert.NotNull(headerPrefix);

        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
            ? JsonResponse("""{"data":[{"id":"m1"}]}""")
            : JsonResponse("""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), new FakeSecrets("private-key"));
        var profile = new AiProfile("profile-1", "Test", "openai-compatible", new Uri("https://example.test/v1/"), "m1", AiAuthMode.SecretReference, "secret-1");
        headerName.SetValue(profile, "x-api-key");
        headerPrefix.SetValue(profile, "");

        var models = await provider.GetModelsAsync(profile);
        var response = await provider.SendAsync(profile, new AiRequest("profile-1", [new AiMessage(AiMessageRole.User, "hello")]));

        Assert.Equal("m1", Assert.Single(models).Id);
        Assert.Equal("private-key", handler.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Null(handler.Requests[0].Headers.Authorization);
        Assert.Equal("private-key", handler.Requests[1].Headers.GetValues("x-api-key").Single());
        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task Invalid_header_prefix_is_rejected_before_the_secret_is_attached()
    {
        var headerName = typeof(AiProfile).GetProperty("SecretHeaderName");
        Assert.NotNull(headerName);
        var headerPrefix = typeof(AiProfile).GetProperty("SecretHeaderPrefix");
        Assert.NotNull(headerPrefix);
        var handler = new RecordingHandler(_ => JsonResponse("""{"data":[]}"""));
        var provider = new OpenAiCompatibleProvider(new HttpClient(handler), new FakeSecrets("private-key"));
        var profile = new AiProfile("profile-1", "Test", "openai-compatible", new Uri("https://example.test/v1/"), "m1", AiAuthMode.SecretReference, "secret-1");
        headerName.SetValue(profile, "x-api-key");
        headerPrefix.SetValue(profile, "\r\nX-Injected: yes");

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(profile));

        Assert.Equal(AiErrorCode.Configuration, error.Code);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("private-key", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FakeSecrets(string value) : IAiSecretResolver
    {
        public ValueTask<string?> ResolveAsync(string secretReference, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(value);
    }
}
