namespace Omega.AI.Gateway.Security;

public interface IAiSecretResolver
{
    ValueTask<string?> ResolveAsync(string secretReference, CancellationToken cancellationToken = default);
}
