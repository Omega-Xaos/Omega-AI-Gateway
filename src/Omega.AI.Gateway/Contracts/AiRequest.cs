namespace Omega.AI.Gateway.Contracts;

public enum AiResponseFormat
{
    Text,
    Json
}

public sealed record AiRequest(
    string ProfileId,
    IReadOnlyList<AiMessage> Messages,
    string? ModelId = null,
    AiResponseFormat ResponseFormat = AiResponseFormat.Text);
