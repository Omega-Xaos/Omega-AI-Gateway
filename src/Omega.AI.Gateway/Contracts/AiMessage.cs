namespace Omega.AI.Gateway.Contracts;

public enum AiMessageRole
{
    System,
    Developer,
    User,
    Assistant
}

public sealed record AiMessage(AiMessageRole Role, string Content);
