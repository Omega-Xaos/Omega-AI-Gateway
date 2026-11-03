namespace Omega.AI.Gateway.Errors;

public enum AiErrorCode
{
    Configuration,
    UnsupportedCapability,
    Authentication,
    Authorization,
    RateLimit,
    NetworkFailure,
    ProviderFailure,
    Timeout,
    MalformedResponse
}

public sealed class AiGatewayException(AiErrorCode code, string message, int? httpStatusCode = null) : Exception(message)
{
    public AiErrorCode Code { get; } = code;
    public int? HttpStatusCode { get; } = httpStatusCode;
}
