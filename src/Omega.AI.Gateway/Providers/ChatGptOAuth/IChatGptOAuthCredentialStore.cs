namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public interface IChatGptOAuthCredentialStore
{
    Task<ChatGptOAuthCredential?> GetAsync(string profileId, CancellationToken cancellationToken = default);
    Task SaveAsync(string profileId, ChatGptOAuthCredential credential, CancellationToken cancellationToken = default);
    Task DeleteAsync(string profileId, CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> AcquireRefreshLockAsync(string profileId, CancellationToken cancellationToken = default);
}
