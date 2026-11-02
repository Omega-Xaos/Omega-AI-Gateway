# Project-specific development instructions

- Preserve the provider-neutral contract; keep consumer-domain types out of this library.
- Do not add ChatGPT website automation, cookies, private endpoints, API-key creation, OAuth fallback, or limit evasion.
- Do not use real provider accounts or credentials in automated tests.
- Add new provider integrations as explicit `IAiProvider` adapters and register them through `AiGatewayBuilder`.
- Provider endpoints and authentication rules must be validated before attaching credentials.
- Run `dotnet test Omega.AI.Gateway.sln` and `dotnet pack src/Omega.AI.Gateway/Omega.AI.Gateway.csproj -c Release -o artifacts/packages` for release-level validation.
