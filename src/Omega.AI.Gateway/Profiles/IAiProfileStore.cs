using Omega.AI.Gateway.Contracts;

namespace Omega.AI.Gateway.Profiles;

public interface IAiProfileStore
{
    Task<AiProfile?> GetAsync(string profileId, CancellationToken cancellationToken = default);
}
