namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

internal sealed record OAuthCallback(string? Code, string? State, string? ClientId, string? Error, string? ErrorDescription);

public sealed record ChatGptOAuthSignIn(string ProfileId, Uri AuthorizationUri, Uri CallbackUri, DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => $"{nameof(ChatGptOAuthSignIn)} {{ ProfileId = {ProfileId}, ExpiresAtUtc = {ExpiresAtUtc}, Authorization = [REDACTED] }}";
}
