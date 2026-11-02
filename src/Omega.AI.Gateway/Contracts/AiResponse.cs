namespace Omega.AI.Gateway.Contracts;

public sealed record AiUsage(int? InputTokens = null, int? OutputTokens = null, int? TotalTokens = null);

public sealed record AiResponse(
    string Text,
    string ModelId,
    string? FinishReason = null,
    AiUsage? Usage = null,
    string? ProviderRequestId = null);
