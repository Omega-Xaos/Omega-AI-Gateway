namespace Omega.AI.Gateway.Contracts;

public enum AiAuthMode
{
    None,
    SecretReference,
    ChatGptOAuth
}

public sealed record AiProfile(
    string Id,
    string DisplayName,
    string ProviderKind,
    Uri BaseUri,
    string ModelId,
    AiAuthMode AuthMode,
    string? SecretReference = null,
    TimeSpan? Timeout = null,
    bool IsEnabled = true,
    bool AllowLocalHttp = false);
