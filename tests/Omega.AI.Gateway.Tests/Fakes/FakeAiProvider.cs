using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Profiles;

namespace Omega.AI.Gateway.Tests.Fakes;

internal sealed class FakeAiProfileStore(params AiProfile?[] profiles) : IAiProfileStore
{
    public static AiProfile DefaultProfile { get; } = new(
        "profile-1", "Test", "fake", new Uri("https://localhost/"), "model-1", AiAuthMode.None);

    public Task<AiProfile?> GetAsync(string profileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(profiles.FirstOrDefault(profile => profile?.Id == profileId));
}

internal sealed class FakeAiProvider : IAiProvider
{
    public string ProviderKind => "fake";
    public int CallCount { get; private set; }
    public TimeSpan Delay { get; init; }
    public Exception? Failure { get; init; }
    public Exception? StreamCreationFailure { get; init; }
    public bool StreamDisposalFailure { get; init; }
    public AiRequest? LastRequest { get; private set; }

    public async Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(AiProfile profile, CancellationToken cancellationToken = default)
    {
        await WaitAsync(cancellationToken);
        return [new AiModelInfo("model-1")];
    }

    public async Task<AiConnectionTest> TestConnectionAsync(AiProfile profile, CancellationToken cancellationToken = default)
    {
        await WaitAsync(cancellationToken);
        return new AiConnectionTest(true, ModelId: profile.ModelId);
    }

    public async Task<AiResponse> SendAsync(AiProfile profile, AiRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        await WaitAsync(cancellationToken);
        return new AiResponse("ok", request.ModelId ?? profile.ModelId);
    }

    public IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiProfile profile,
        AiRequest request,
        CancellationToken cancellationToken = default)
    {
        if (StreamCreationFailure is not null)
        {
            throw StreamCreationFailure;
        }

        return StreamCoreAsync(cancellationToken);
    }

    private async IAsyncEnumerable<AiStreamEvent> StreamCoreAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await WaitAsync(cancellationToken);
            yield return new AiStreamEvent(AiStreamEventKind.TextDelta, "ok");
            yield return new AiStreamEvent(AiStreamEventKind.Completed);
        }
        finally
        {
            if (StreamDisposalFailure)
            {
                throw new InvalidOperationException("sentinel-stream-dispose-secret");
            }
        }
    }

    private async Task WaitAsync(CancellationToken token)
    {
        CallCount++;
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, token);
        }

        if (Failure is not null)
        {
            throw Failure;
        }
    }
}
