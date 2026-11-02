namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public sealed record ChatGptOAuthCredential(
    string ClientId, string HostId, string Subject, string? Email,
    string AccessToken, string RefreshToken, string IdToken,
    DateTimeOffset ExpiresAtUtc, IReadOnlySet<string> Scopes,
    string? PendingIdToken = null)
{
    public override string ToString() => $"{nameof(ChatGptOAuthCredential)} {{ Account = [REDACTED], Tokens = [REDACTED] }}";
}
