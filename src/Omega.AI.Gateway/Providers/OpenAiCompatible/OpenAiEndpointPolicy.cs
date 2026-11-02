using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Errors;

namespace Omega.AI.Gateway.Providers.OpenAiCompatible;

internal static class OpenAiEndpointPolicy
{
    public static Uri Build(AiProfile profile, string relativePath)
    {
        var baseUri = profile.BaseUri;
        if (!baseUri.IsAbsoluteUri || baseUri.UserInfo.Length > 0 || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "Provider base URL is invalid.");
        }

        var localHttp = baseUri.Scheme == Uri.UriSchemeHttp &&
                        profile.AllowLocalHttp &&
                        (baseUri.IsLoopback || string.Equals(baseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        if (baseUri.Scheme != Uri.UriSchemeHttps && !localHttp)
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "Provider URL must use HTTPS, except explicitly allowed loopback HTTP.");
        }

        var normalizedBase = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(normalizedBase, relativePath);
    }
}
