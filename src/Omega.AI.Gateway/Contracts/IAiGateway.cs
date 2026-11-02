namespace Omega.AI.Gateway.Contracts;

public interface IAiGateway
{
    Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(string profileId, CancellationToken cancellationToken = default);
    Task<AiConnectionTest> TestConnectionAsync(string profileId, CancellationToken cancellationToken = default);
    Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, CancellationToken cancellationToken = default);
}
