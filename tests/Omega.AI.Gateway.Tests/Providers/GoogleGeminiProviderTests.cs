using System.Net;
using System.Text;
using System.Text.Json;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Providers.GoogleGemini;
using Omega.AI.Gateway.Security;

namespace Omega.AI.Gateway.Tests.Providers;

public sealed class GoogleGeminiProviderTests
{
    [Fact]
    public async Task Model_catalog_filters_for_generate_content_and_uses_api_key_header()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"models":[{"name":"models/gemini-2.5-flash","displayName":"Gemini Flash","description":"Fast","supportedGenerationMethods":["generateContent"]},{"name":"models/embedding-001","displayName":"Embedding","supportedGenerationMethods":["embedContent"]}]}"""));
        var provider = CreateProvider(handler);

        var models = await provider.GetModelsAsync(Profile());

        var model = Assert.Single(models);
        Assert.Equal("models/gemini-2.5-flash", model.Id);
        Assert.Equal("Gemini Flash", model.DisplayName);
        Assert.Equal("Fast", model.Description);
        Assert.Equal("private-key", handler.Requests[0].Headers.GetValues("x-goog-api-key").Single());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models?pageSize=100", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Send_maps_system_messages_and_json_mode_and_uses_selected_model_safely()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"candidates":[{"content":{"parts":[{"text":"{\"ok\":true}"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":4,"totalTokenCount":16}}"""));
        var provider = CreateProvider(handler);
        var profile = Profile();
        var aiRequest = new AiRequest("profile-1", [
            new AiMessage(AiMessageRole.System, "system instructions"),
            new AiMessage(AiMessageRole.Developer, "developer details"),
            new AiMessage(AiMessageRole.User, "make a scene"),
            new AiMessage(AiMessageRole.Assistant, "previous answer")
        ], "models/gemini-2.5-flash", AiResponseFormat.Json);

        var response = await provider.SendAsync(profile, aiRequest);

        Assert.Equal("{\"ok\":true}", response.Text);
        Assert.Equal("models/gemini-2.5-flash", response.ModelId);
        Assert.Equal("STOP", response.FinishReason);
        Assert.Equal(new AiUsage(12, 4, 16), response.Usage);
        var sent = handler.Requests[0];
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", sent.RequestUri!.AbsoluteUri);
        using var payload = JsonDocument.Parse(handler.ContentBodies[0]);
        Assert.Equal("system instructions\n\ndeveloper details", payload.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("user", payload.RootElement.GetProperty("contents")[0].GetProperty("role").GetString());
        Assert.Equal("model", payload.RootElement.GetProperty("contents")[1].GetProperty("role").GetString());
        Assert.Equal("application/json", payload.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
    }

    [Fact]
    public async Task Model_catalog_follows_page_tokens()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Query.Contains("pageToken=next", StringComparison.Ordinal)
            ? JsonResponse("""{"models":[{"name":"models/gemini-2.0-flash","supportedGenerationMethods":["generateContent"]}]}""")
            : JsonResponse("""{"models":[{"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent"]}],"nextPageToken":"next"}"""));
        var provider = CreateProvider(handler);

        var models = await provider.GetModelsAsync(Profile());

        Assert.Equal(2, models.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("pageToken=next", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_base_url_is_rejected_before_secret_resolution()
    {
        var secrets = new CountingSecrets();
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var provider = new GoogleGeminiProvider(new HttpClient(handler), secrets);
        var invalid = Profile() with { BaseUri = new Uri("http://not-local.example/") };

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(invalid));

        Assert.Equal(AiErrorCode.Configuration, error.Code);
        Assert.Equal(0, secrets.ResolutionCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Invalid_model_path_is_rejected_before_secret_resolution()
    {
        var secrets = new CountingSecrets();
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var provider = new GoogleGeminiProvider(new HttpClient(handler), secrets);

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.SendAsync(
            Profile(), new AiRequest("profile-1", [new AiMessage(AiMessageRole.User, "hello")], "models/../other")));

        Assert.Equal(AiErrorCode.Configuration, error.Code);
        Assert.Equal(0, secrets.ResolutionCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Redirect_response_is_rejected()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = new Uri("https://attacker.example/collect") }
        });
        var provider = CreateProvider(handler);

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(Profile()));

        Assert.Equal(AiErrorCode.Authorization, error.Code);
    }

    [Fact]
    public async Task Response_from_another_origin_is_rejected()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = JsonResponse("{} ");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://attacker.example/collect");
            return response;
        });
        var provider = CreateProvider(handler);

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(Profile()));

        Assert.Equal(AiErrorCode.Authorization, error.Code);
    }

    [Fact]
    public async Task Connection_test_uses_model_catalog()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"models":[{"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent"]}]}"""));
        var provider = CreateProvider(handler);

        var result = await provider.TestConnectionAsync(Profile());

        Assert.True(result.Succeeded);
        Assert.Equal("models/gemini-2.5-flash", result.ModelId);
    }

    [Fact]
    public async Task Provider_http_errors_are_normalized_without_body_or_key_leaks()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("private-key rejected", Encoding.UTF8, "text/plain")
        });
        var provider = CreateProvider(handler);

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => provider.GetModelsAsync(Profile()));

        Assert.Equal(AiErrorCode.Authentication, error.Code);
        Assert.DoesNotContain("private-key", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_emits_text_deltas_and_completion()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hello\"}]}}]}\n\ndata: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\" world\"}]},\"finishReason\":\"STOP\"}]}\n\n", Encoding.UTF8, "text/event-stream")
        });
        var provider = CreateProvider(handler);

        var events = new List<AiStreamEvent>();
        await foreach (var item in provider.StreamAsync(Profile(), new AiRequest("profile-1", [new AiMessage(AiMessageRole.User, "hello")], "gemini-2.5-flash")))
            events.Add(item);

        Assert.Equal([AiStreamEventKind.TextDelta, AiStreamEventKind.TextDelta, AiStreamEventKind.Completed], events.Select(item => item.Kind));
        Assert.Equal("Hello world", string.Concat(events.Where(item => item.Kind == AiStreamEventKind.TextDelta).Select(item => item.Text)));
        Assert.Contains("streamGenerateContent?alt=sse", handler.Requests[0].RequestUri!.PathAndQuery, StringComparison.Ordinal);
    }

    private static GoogleGeminiProvider CreateProvider(RecordingHandler handler) =>
        new(new HttpClient(handler), new FixedSecrets("private-key"));

    private static AiProfile Profile() => new(
        "profile-1", "Test", "google-gemini", new Uri("https://generativelanguage.googleapis.com/"),
        "models/gemini-2.5-flash", AiAuthMode.SecretReference, "secret-1");

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> ContentBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            ContentBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return responder(request);
        }
    }

    private class FixedSecrets(string? value) : IAiSecretResolver
    {
        public virtual ValueTask<string?> ResolveAsync(string secretReference, CancellationToken cancellationToken = default) => ValueTask.FromResult(value);
    }

    private sealed class CountingSecrets : FixedSecrets
    {
        public CountingSecrets() : base("private-key") { }
        public int ResolutionCount { get; private set; }
        public override ValueTask<string?> ResolveAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            ResolutionCount++;
            return base.ResolveAsync(secretReference, cancellationToken);
        }
    }
}
