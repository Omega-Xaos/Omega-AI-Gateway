namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public interface IAuthorizationBrowser
{
    Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default);
}
