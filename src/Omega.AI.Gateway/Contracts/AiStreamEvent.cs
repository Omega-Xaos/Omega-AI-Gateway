namespace Omega.AI.Gateway.Contracts;

public enum AiStreamEventKind
{
    TextDelta,
    Completed,
    Failed
}

public sealed record AiStreamEvent(
    AiStreamEventKind Kind,
    string? Text = null,
    AiResponse? Response = null,
    string? ErrorCode = null,
    string? Message = null);
