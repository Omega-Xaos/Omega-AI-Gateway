using System.Diagnostics;
using Omega.AI.Gateway.Errors;

namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public sealed class SystemAuthorizationBrowser : IAuthorizationBrowser
{
    public Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        }
        catch (Exception)
        {
            throw new AiGatewayException(AiErrorCode.Authorization, "Could not open the system browser for sign-in.");
        }
    }
}
