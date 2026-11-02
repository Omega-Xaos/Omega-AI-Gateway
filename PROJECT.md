# Project

## Purpose

Reusable .NET 10 AI transport library for Omega Lab applications. The Gateway provides a stable provider-neutral contract and leaves domain validation and behavior to each consuming application.

## Components

- `Omega.AI.Gateway`: contracts, profile routing, dependency injection and provider adapters.
- `Omega.AI.Gateway.Tests`: offline contract, routing, protocol, OAuth and security tests.

## Invariants

- Keep provider-specific wire protocols behind `IAiProvider`.
- Hosts own profile persistence, secret resolution and protected OAuth credential persistence.
- Never put API keys or OAuth tokens in profiles, source, test fixtures with real credentials, or diagnostics.
- OAuth plan usage is optional, explicitly user-authorized and subject to OpenAI's current eligibility and usage limits.
- ChatGPT OAuth calls only official endpoints; inference uses Responses with `store=false`, `stream=true` and requires `response.completed`.
- Network redirects are disabled in the built-in `AddOpenAiCompatible` and `AddChatGptOAuth` registrations.
- Tests must remain offline and use generated synthetic identities and tokens.

## Scope boundary

This repository does not implement the employer's SVG composition service, import Figma assets, choose semantic assets, edit scenes or export SVG. The AI Gateway is an integration component for that future consumer.

## Dependencies

- .NET 10.
- Microsoft.Extensions.DependencyInjection and Microsoft.Extensions.Http.
- System.IdentityModel.Tokens.Jwt for OAuth ID-token signature and claim validation.
- xUnit for deterministic tests.
