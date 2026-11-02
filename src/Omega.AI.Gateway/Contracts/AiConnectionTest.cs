namespace Omega.AI.Gateway.Contracts;

public sealed record AiConnectionTest(bool Succeeded, string? Message = null, string? ModelId = null);
