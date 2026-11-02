using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;
using Omega.AI.Gateway.Profiles;

namespace Omega.AI.Gateway.Gateway;

public sealed class AiGateway : IAiGateway
{
    private readonly IAiProfileStore _profiles;
    private readonly IReadOnlyDictionary<string, IAiProvider> _providers;

    public AiGateway(IAiProfileStore profiles, IEnumerable<IAiProvider> providers)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        ArgumentNullException.ThrowIfNull(providers);
        var providerMap = new Dictionary<string, IAiProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (provider is null || string.IsNullOrWhiteSpace(provider.ProviderKind) ||
                !providerMap.TryAdd(provider.ProviderKind, provider))
            {
                throw new AiGatewayException(AiErrorCode.Configuration, "Provider registrations must have unique, non-empty kinds.");
            }
        }

        _providers = providerMap;
    }

    public Task<IReadOnlyList<AiModelInfo>> GetModelsAsync(string profileId, CancellationToken cancellationToken = default) =>
        WithProfileAsync(profileId, (provider, profile, token) => provider.GetModelsAsync(profile, token), cancellationToken);

    public Task<AiConnectionTest> TestConnectionAsync(string profileId, CancellationToken cancellationToken = default) =>
        WithProfileAsync(profileId, (provider, profile, token) => provider.TestConnectionAsync(profile, token), cancellationToken);

    public Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        return WithProfileAsync(request.ProfileId,
            (provider, profile, token) => provider.SendAsync(profile, request, token), cancellationToken);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var (provider, profile) = await ResolveAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
        using var timeout = CreateTimeout(profile, cancellationToken, out var linkedToken);
        IAsyncEnumerator<AiStreamEvent> enumerator;
        try
        {
            enumerator = provider.StreamAsync(profile, request, linkedToken).GetAsyncEnumerator(linkedToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
        {
            throw TimeoutException();
        }
        catch (AiGatewayException exception)
        {
            throw NormalizeProviderError(exception.Code);
        }
        catch (Exception)
        {
            throw ProviderFailure();
        }

        try
        {
            while (true)
            {
                bool hasNext;
                AiStreamEvent current;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    current = hasNext ? enumerator.Current : null!;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout?.IsCancellationRequested == true)
                {
                    throw TimeoutException();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (AiGatewayException exception)
                {
                    throw NormalizeProviderError(exception.Code);
                }
                catch (Exception)
                {
                    throw ProviderFailure();
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return current;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout?.IsCancellationRequested == true)
            {
                throw TimeoutException();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AiGatewayException exception)
            {
                throw NormalizeProviderError(exception.Code);
            }
            catch (Exception)
            {
                throw ProviderFailure();
            }
        }
    }

    private async Task<T> WithProfileAsync<T>(
        string profileId,
        Func<IAiProvider, AiProfile, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var (provider, profile) = await ResolveAsync(profileId, cancellationToken).ConfigureAwait(false);
        using var timeout = CreateTimeout(profile, cancellationToken, out var linkedToken);
        try
        {
            return await operation(provider, profile, linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout?.IsCancellationRequested == true)
        {
            throw TimeoutException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AiGatewayException exception)
        {
            throw NormalizeProviderError(exception.Code);
        }
        catch (Exception)
        {
            throw ProviderFailure();
        }
    }

    private async Task<(IAiProvider Provider, AiProfile Profile)> ResolveAsync(string profileId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "Profile ID is required.");
        }

        AiProfile? profile;
        try
        {
            profile = await _profiles.GetAsync(profileId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "AI profile could not be loaded.");
        }

        if (profile is null || !profile.IsEnabled || string.IsNullOrWhiteSpace(profile.ProviderKind) ||
            string.IsNullOrWhiteSpace(profile.ModelId) || profile.BaseUri is null)
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "AI profile is missing, disabled, or invalid.");
        }

        if (!_providers.TryGetValue(profile.ProviderKind, out var provider))
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "No provider is registered for the profile.");
        }

        return (provider, profile);
    }

    private static void ValidateRequest(AiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileId) || request.Messages is null || request.Messages.Count == 0 ||
            request.Messages.Any(message => message is null || string.IsNullOrWhiteSpace(message.Content)) ||
            request.ModelId is not null && string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "AI request is invalid.");
        }
    }

    private static CancellationTokenSource? CreateTimeout(
        AiProfile profile,
        CancellationToken callerToken,
        out CancellationToken linkedToken)
    {
        if (profile.Timeout is not { } duration)
        {
            linkedToken = callerToken;
            return null;
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "AI profile timeout must be positive.");
        }

        var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(duration);
        linkedToken = timeout.Token;
        return timeout;
    }

    private static AiGatewayException ProviderFailure() =>
        new(AiErrorCode.ProviderFailure, "AI provider operation failed.");

    private static AiGatewayException NormalizeProviderError(AiErrorCode code) => new(code, code switch
    {
        AiErrorCode.Configuration => "AI provider configuration is invalid.",
        AiErrorCode.UnsupportedCapability => "AI provider does not support this operation.",
        AiErrorCode.Authentication => "AI provider authentication failed.",
        AiErrorCode.Authorization => "AI provider authorization failed.",
        AiErrorCode.RateLimit => "AI provider rate limit was reached.",
        AiErrorCode.NetworkFailure => "AI provider network request failed.",
        AiErrorCode.Timeout => "AI provider operation timed out.",
        AiErrorCode.MalformedResponse => "AI provider returned malformed data.",
        _ => "AI provider operation failed."
    });

    private static AiGatewayException TimeoutException() =>
        new(AiErrorCode.Timeout, "AI provider operation timed out.");
}



