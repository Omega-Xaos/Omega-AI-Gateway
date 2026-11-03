using System.Net;
using System.Text;
using System.Text.Json;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Providers.Anthropic;
using Omega.AI.Gateway.Security;

namespace Omega.AI.Gateway.Tests.Providers;

public sealed class AnthropicMessagesProviderTests
{
    [Fact]
    public async Task Model_discovery_uses_native_auth_and_follows_cursor_pages()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Query.Contains("after_id", StringComparison.Ordinal)
            ? JsonResponse("""{"data":[{"id":"claude-two","display_name":"Claude Two"}],"has_more":false}""")
            : JsonResponse("""{"data":[{"id":"claude-one","display_name":"Claude One"}],"has_more":true,"last_id":"claude-one"}"""));
        var provider = new AnthropicMessagesProvider(new HttpClient(handler), new FakeSecrets("test-secret"));

        var models = await provider.GetModelsAsync(Profile());

        Assert.Equal(["claude-one", "claude-two"], models.Select(model => model.Id));
        Assert.Equal("Claude Two", models[1].DisplayName);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.Equal("test-secret", request.Headers["x-api-key"]);
            Assert.Equal("2026-10-21", request.Headers["anthropic-version"]);
        });
        Assert.Contains("limit=1000", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("after_id=claude-one", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_maps_system_developer_and_conversation_and_parses_usage()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {"id":"msg_123","model":"claude-sonnet-test","role":"assistant","content":[{"type":"text","text":"{\"ok\":true}"}],"stop_reason":"end_turn","usage":{"input_tokens":17,"output_tokens":9}}
            """));
        var provider = new AnthropicMessagesProvider(new HttpClient(handler), new FakeSecrets("test-secret"));
        var request = new AiRequest("api-test", [
            new AiMessage(AiMessageRole.System, "system rule"),
            new AiMessage(AiMessageRole.Developer, "developer rule"),
            new AiMessage(AiMessageRole.User, "first"),
            new AiMessage(AiMessageRole.Assistant, "previous"),
            new AiMessage(AiMessageRole.User, "latest")],
            "claude-selected", AiResponseFormat.Json);

        var result = await provider.SendAsync(Profile(), request);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("https://api.anthropic.test/v1/messages", sent.Uri.AbsoluteUri);
        Assert.Equal("test-secret", sent.Headers["x-api-key"]);
        Assert.Equal("application/json", sent.Headers["content-type"].Split(';', 2)[0].Trim());
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("claude-selected", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("system rule\n\ndeveloper rule", body.RootElement.GetProperty("system").GetString());
        Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("first", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("latest", messages[2].GetProperty("content").GetString());
        Assert.Equal("{\"ok\":true}", result.Text);
        Assert.Equal("claude-sonnet-test", result.ModelId);
        Assert.Equal("end_turn", result.FinishReason);
        Assert.Equal(new AiUsage(17, 9, 26), result.Usage);
        Assert.Equal("msg_123", result.ProviderRequestId);
    }

    [Fact]
    public async Task Invalid_endpoint_is_rejected_before_secret_resolution()
    {
        var secrets = new FakeSecrets("test-secret");
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var provider = new AnthropicMessagesProvider(new HttpClient(handler), secrets);

        var error = await Assert.ThrowsAsync<AiGatewayException>(() =>
            provider.GetModelsAsync(Profile(baseUri: new Uri("http://api.anthropic.test/"))));

        Assert.Equal(AiErrorCode.Configuration, error.Code);
        Assert.Equal(0, secrets.ResolveCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Missing_key_fails_without_sending_request()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var provider = new AnthropicMessagesProvider(new HttpClient(handler), new FakeSecrets(null));

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(Profile()));

        Assert.Equal(AiErrorCode.Authentication, error.Code);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("test-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_emits_text_deltas_and_completion()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n\n" +
                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
                Encoding.UTF8,
                "text/event-stream")
        });
        var provider = new AnthropicMessagesProvider(new HttpClient(handler), new FakeSecrets("test-secret"));

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(Profile(), new AiRequest("api-test", [new AiMessage(AiMessageRole.User, "hello")]))){ events.Add(item); }

        Assert.Equal([AiStreamEventKind.TextDelta, AiStreamEventKind.Completed], events.Select(item => item.Kind));
        Assert.Equal("hello", events[0].Text);
        using var requestBody = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.True(requestBody.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Redirect_is_rejected_without_following_it()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect));
        var provider = new AnthropicMessagesProvider(new HttpClient(handler), new FakeSecrets("test-secret"));

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(Profile()));

        Assert.Equal(AiErrorCode.Authorization, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Provider_error_does_not_echo_response_body_or_secret()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("test-secret was rejected")
        });
        var provider = new AnthropicMessagesProvider(new HttpClient(handler), new FakeSecrets("test-secret"));

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(Profile()));

        Assert.Equal(AiErrorCode.Authentication, error.Code);
        Assert.DoesNotContain("test-secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("was rejected", error.Message, StringComparison.Ordinal);
    }

    private static AiProfile Profile(Uri? baseUri = null) => new(
        "api-test", "Anthropic", "anthropic-messages", baseUri ?? new Uri("https://api.anthropic.test/"),
        "claude-test", AiAuthMode.SecretReference, "secret-reference");

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => header.Value.Single(), StringComparer.OrdinalIgnoreCase);
            if (request.Content is not null)
                foreach (var header in request.Content.Headers) headers[header.Key.ToLowerInvariant()] = header.Value.Single();
            Requests.Add(new CapturedRequest(request.Method.Method, request.RequestUri!, headers, body));
            return responder(request);
        }
    }

    private sealed record CapturedRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);

    private sealed class FakeSecrets(string? value) : IAiSecretResolver
    {
        public int ResolveCount { get; private set; }

        public ValueTask<string?> ResolveAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return ValueTask.FromResult(value);
        }
    }
}
