namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public sealed class ChatGptOAuthOptions
{
    public string ApplicationName { get; init; } = "Omega AI Gateway";
    public Uri AuthorizationEndpoint { get; init; } = new("https://auth.openai.com/api/accounts/authorize");
    public Uri TokenEndpoint { get; init; } = new("https://auth.openai.com/api/accounts/oauth/token");
    public Uri JwksEndpoint { get; init; } = new("https://auth.openai.com/.well-known/jwks.json");
    public Uri ApiBaseUri { get; init; } = new("https://api.openai.com/v1/");
    public string Resource { get; init; } = "https://api.openai.com/v1";
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RefreshSkew { get; init; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApplicationName) || ApplicationName.Length > 80 ||
            SignInTimeout <= TimeSpan.Zero || SignInTimeout > TimeSpan.FromMinutes(10) || RefreshSkew < TimeSpan.Zero ||
            !IsOfficial(AuthorizationEndpoint, "auth.openai.com", "/api/accounts/authorize") ||
            !IsOfficial(TokenEndpoint, "auth.openai.com", "/api/accounts/oauth/token") ||
            !IsOfficial(JwksEndpoint, "auth.openai.com", "/.well-known/jwks.json") ||
            !IsOfficial(ApiBaseUri, "api.openai.com", "/v1/") || Resource != "https://api.openai.com/v1")
        {
            throw new ArgumentException("ChatGPT OAuth options must use the official OpenAI endpoints and valid limits.");
        }
    }

    private static bool IsOfficial(Uri uri, string host, string path) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) && uri.Port == 443 && uri.AbsolutePath == path &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
