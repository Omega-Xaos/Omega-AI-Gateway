namespace Omega.AI.Gateway.Contracts;

public interface IAiProvider
{
    string ProviderKind { get; }
    Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(AiProfile profile, CancellationToken cancellationToken = default);
    Task<AiConnectionTest> TestConnectionAsync(AiProfile profile, CancellationToken cancellationToken = default);
    Task<AiResponse> SendAsync(AiProfile profile, AiRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProfile profile, AiRequest request, CancellationToken cancellationToken = default);
}
