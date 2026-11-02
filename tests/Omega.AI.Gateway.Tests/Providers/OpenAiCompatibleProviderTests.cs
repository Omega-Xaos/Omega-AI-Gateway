using System.Net;
using System.Text;
using System.Text.Json;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Providers.OpenAiCompatible;
using Omega.AI.Gateway.Security;
using Xunit;

namespace Omega.AI.Gateway.Tests.Providers;

public sealed class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task Completion_maps_messages_model_usage_and_bearer_secret()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {"id":"req-1","model":"gpt-test","choices":[{"message":{"content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
            """));
        var provider = Create(handler, new FakeSecrets("private-token"));
        var profile = Profile(AiAuthMode.SecretReference, "secret-ref");
        var request = new AiRequest(profile.Id, [new AiMessage(AiMessageRole.User, "hi")], ResponseFormat: AiResponseFormat.Json);

        var response = await provider.SendAsync(profile, request);

        Assert.Equal("Bearer private-token", handler.LastRequest!.Headers.Authorization!.ToString());
        Assert.Equal("gpt-test", response.ModelId);
        Assert.Equal(5, response.Usage!.TotalTokens);
        Assert.Equal("req-1", response.ProviderRequestId);
        using var json = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("configured", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("user", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Model_list_and_connection_check_use_models_endpoint()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"data":[{"id":"m1"},{"id":"m2"}]}"""));
        var provider = Create(handler);

        var models = await provider.GetModelsAsync(Profile());
        var test = await provider.TestConnectionAsync(Profile());

        Assert.Equal(new[] { "m1", "m2" }, models.Select(model => model.Id));
        Assert.True(test.Succeeded);
        Assert.EndsWith("/v1/models", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Missing_model_catalog_falls_back_to_configured_model()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = Create(handler);

        var models = await provider.GetModelsAsync(Profile());

        Assert.Equal("configured", Assert.Single(models).Id);
    }

    [Fact]
    public async Task Connection_test_does_not_claim_success_when_model_discovery_is_missing()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = Create(handler);

        var result = await provider.TestConnectionAsync(Profile());

        Assert.False(result.Succeeded);
        Assert.Contains("not verified", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Http_failure_is_normalized_without_response_body_leak()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("sensitive provider diagnostic")
        });
        var provider = Create(handler);

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.SendAsync(
            Profile(), new AiRequest("profile-1", [new AiMessage(AiMessageRole.User, "hi")])));

        Assert.Equal(AiErrorCode.RateLimit, error.Code);
        Assert.DoesNotContain("sensitive provider diagnostic", error.Message);
    }

    [Fact]
    public async Task Stream_translates_text_deltas_and_terminal_event()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"B\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        });
        var provider = Create(handler);

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(Profile(), new AiRequest("profile-1", [new AiMessage(AiMessageRole.User, "hi")])))
        {
            events.Add(item);
        }

        Assert.Equal(new[] { AiStreamEventKind.TextDelta, AiStreamEventKind.TextDelta, AiStreamEventKind.Completed }, events.Select(item => item.Kind));
        Assert.Equal(new[] { "A", "B" }, events.Take(2).Select(item => item.Text));
    }

    private static OpenAiCompatibleProvider Create(RecordingHandler handler, IAiSecretResolver? secrets = null) =>
        new(new HttpClient(handler), secrets ?? new FakeSecrets(null));

    private static AiProfile Profile(AiAuthMode mode = AiAuthMode.None, string? secret = null) =>
        new("profile-1", "Test", "openai-compatible", new Uri("https://example.test/v1/"), "configured", mode, secret);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }

    private sealed class FakeSecrets(string? value) : IAiSecretResolver
    {
        public ValueTask<string?> ResolveAsync(string secretReference, CancellationToken cancellationToken = default) => ValueTask.FromResult(value);
    }
}
