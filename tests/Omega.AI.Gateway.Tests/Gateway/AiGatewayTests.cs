using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Gateway;
using Omega.AI.Gateway.Tests.Fakes;
using Xunit;

namespace Omega.AI.Gateway.Tests.Gateway;

public sealed class AiGatewayTests
{
    [Fact]
    public async Task Send_resolves_profile_and_routes_model_override()
    {
        var profile = FakeAiProfileStore.DefaultProfile with { ModelId = "configured" };
        var provider = new FakeAiProvider();
        var gateway = CreateGateway(profile, provider);
        var request = new AiRequest(profile.Id, [new AiMessage(AiMessageRole.User, "hi")], "override");

        var response = await gateway.SendAsync(request);

        Assert.Equal("override", provider.LastRequest?.ModelId);
        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task Disabled_profile_is_rejected()
    {
        var profile = FakeAiProfileStore.DefaultProfile with { IsEnabled = false };
        var gateway = CreateGateway(profile, new FakeAiProvider());

        var error = await Assert.ThrowsAsync<AiGatewayException>(() =>
            gateway.GetModelsAsync(profile.Id));

        Assert.Equal(AiErrorCode.Configuration, error.Code);
    }

    [Fact]
    public async Task Unknown_profile_is_rejected_without_provider_call()
    {
        var provider = new FakeAiProvider();
        var gateway = CreateGateway(null, provider);

        await Assert.ThrowsAsync<AiGatewayException>(() => gateway.TestConnectionAsync("missing"));

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void Duplicate_provider_kinds_are_rejected()
    {
        var error = Assert.Throws<AiGatewayException>(() => new AiGateway(
            new FakeAiProfileStore(FakeAiProfileStore.DefaultProfile),
            [new FakeAiProvider(), new FakeAiProvider()]));

        Assert.Equal(AiErrorCode.Configuration, error.Code);
    }

    [Fact]
    public async Task Provider_failure_is_normalized_without_leaking_exception_text()
    {
        const string secret = "sentinel-private-value";
        var provider = new FakeAiProvider { Failure = new InvalidOperationException(secret) };
        var gateway = CreateGateway(FakeAiProfileStore.DefaultProfile, provider);

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => gateway.TestConnectionAsync("profile-1"));

        Assert.Equal(AiErrorCode.ProviderFailure, error.Code);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Provider_gateway_exception_message_is_also_redacted()
    {
        const string secret = "sentinel-private-value";
        var provider = new FakeAiProvider { Failure = new AiGatewayException(AiErrorCode.Authentication, secret) };
        var gateway = CreateGateway(FakeAiProfileStore.DefaultProfile, provider);

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => gateway.TestConnectionAsync("profile-1"));

        Assert.Equal(AiErrorCode.Authentication, error.Code);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Equal("AI provider authentication failed.", error.Message);
    }

    [Fact]
    public async Task Stream_factory_exception_is_normalized_without_leaking_message()
    {
        const string secret = "sentinel-stream-create-secret";
        var provider = new FakeAiProvider { StreamCreationFailure = new InvalidOperationException(secret) };
        var gateway = CreateGateway(FakeAiProfileStore.DefaultProfile, provider);
        var request = new AiRequest("profile-1", [new AiMessage(AiMessageRole.User, "hi")]);

        var error = await Assert.ThrowsAsync<AiGatewayException>(async () =>
        {
            await foreach (var _ in gateway.StreamAsync(request))
            {
            }
        });

        Assert.Equal(AiErrorCode.ProviderFailure, error.Code);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stream_disposal_exception_is_normalized_without_leaking_message()
    {
        const string secret = "sentinel-stream-dispose-secret";
        var provider = new FakeAiProvider { StreamDisposalFailure = true };
        var gateway = CreateGateway(FakeAiProfileStore.DefaultProfile, provider);
        var request = new AiRequest("profile-1", [new AiMessage(AiMessageRole.User, "hi")]);
        var enumerator = gateway.StreamAsync(request).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        var error = await Assert.ThrowsAsync<AiGatewayException>(() => enumerator.DisposeAsync().AsTask());

        Assert.Equal(AiErrorCode.ProviderFailure, error.Code);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_timeout_is_reported_as_timeout()
    {
        var profile = FakeAiProfileStore.DefaultProfile with { Timeout = TimeSpan.FromMilliseconds(30) };
        var provider = new FakeAiProvider { Delay = TimeSpan.FromSeconds(2) };
        var gateway = CreateGateway(profile, provider);

        var error = await Assert.ThrowsAsync<AiGatewayException>(() => gateway.TestConnectionAsync(profile.Id));

        Assert.Equal(AiErrorCode.Timeout, error.Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        var profile = FakeAiProfileStore.DefaultProfile;
        var provider = new FakeAiProvider { Delay = TimeSpan.FromSeconds(2) };
        var gateway = CreateGateway(profile, provider);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.TestConnectionAsync(profile.Id, cts.Token));
    }

    private static AiGateway CreateGateway(AiProfile? profile, params IAiProvider[] providers) =>
        new(new FakeAiProfileStore(profile), providers);
}
