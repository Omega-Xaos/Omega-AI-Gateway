# Omega AI Gateway Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone .NET 10 library that gives Omega Lab applications a provider-neutral AI contract, an OpenAI-compatible adapter, and an optional official ChatGPT OAuth adapter for eligible local testing.

**Architecture:** One packable library owns the neutral DTOs, Gateway orchestration, DI registration, and provider adapters. Applications supply profile and protected credential storage. The OAuth adapter owns the short-lived loopback callback and opens the user's system browser through an injectable abstraction; adding another provider means implementing and registering `IAiProvider`, without adding consumer-domain types to the Gateway.

**Tech Stack:** C# / .NET 10, `HttpClient`, `System.Text.Json`, dependency injection, xUnit, and a maintained JWT/OpenID Connect validation package for OAuth ID tokens.

**Spec:** `docs/superpowers/specs/2026-11-02-omega-ai-gateway-design.md`

## Global Constraints

- Target `net10.0` and keep the library independent from OmegaIllustrator, SVG, scenes, and assets.
- Provider-specific protocols live behind `IAiProvider`; do not present every vendor API as one wire protocol.
- Profiles contain a `SecretReference`, not a secret. OAuth tokens live only behind a host-supplied protected credential store.
- Do not use browser automation, cookies, ChatGPT private endpoints, or OAuth billing-path fallback.
- For Sign in with ChatGPT plan usage, call only the public Responses API with `store=false`, `stream=true`; do not report success before `response.completed`.
- ChatGPT plan OAuth is optional local-development/testing functionality subject to current OpenAI eligibility and shared usage limits; ordinary/production use should use supported provider APIs and their documented billing/authentication.
- The user explicitly declined creating an OpenAI API key. Do not provision, configure, inspect, or use one; protocol tests remain offline and live ChatGPT plan testing waits for the user to complete OAuth consent.
- No network credentials or external provider are required for build or tests.

## Review Focus

- Callback forgery or cross-attempt reuse: test wrong/missing state, mismatched client ID, nonce mismatch, and PKCE verifier binding.
- Invalid token or missing plan scope: test JWT issuer/audience/signature/expiry/nonce validation and refusal to infer without `chatgpt.tokens.use.direct`.
- OAuth stream ending early or reporting failure after deltas: test `response.failed`, `response.incomplete`, transport interruption, and success only after `response.completed`.
- Credential leakage/races: test redacted errors, atomic token replacement, and serialized refresh for one credential record.
- Arbitrary endpoint/redirect and unsupported preview options: test HTTPS defaults, explicit loopback HTTP, cross-origin redirect refusal, and omission/rejection of unsupported OAuth fields.

---

### Task 1: Create the standalone workspace and public contract

**Files:**
- Create: `Omega.AI.Gateway.sln`
- Create: `global.json`, `Directory.Build.props`, `.editorconfig`, `.gitignore`
- Create: `src/Omega.AI.Gateway/Omega.AI.Gateway.csproj`
- Create: `src/Omega.AI.Gateway/Contracts/AiProfile.cs`
- Create: `src/Omega.AI.Gateway/Contracts/AiMessage.cs`
- Create: `src/Omega.AI.Gateway/Contracts/AiRequest.cs`
- Create: `src/Omega.AI.Gateway/Contracts/AiResponse.cs`
- Create: `src/Omega.AI.Gateway/Contracts/AiModelInfo.cs`
- Create: `src/Omega.AI.Gateway/Contracts/AiStreamEvent.cs`
- Create: `src/Omega.AI.Gateway/Contracts/AiConnectionTest.cs`
- Create: `src/Omega.AI.Gateway/Contracts/IAiGateway.cs`
- Create: `src/Omega.AI.Gateway/Contracts/IAiProvider.cs`
- Create: `src/Omega.AI.Gateway/Profiles/IAiProfileStore.cs`
- Create: `src/Omega.AI.Gateway/Security/IAiSecretResolver.cs`
- Create: `src/Omega.AI.Gateway/DependencyInjection/AiGatewayBuilder.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Omega.AI.Gateway.Tests.csproj`
- Test: `tests/Omega.AI.Gateway.Tests/Contracts/ContractShapeTests.cs`

**Interfaces:**
- `IAiGateway`: `GetModelsAsync(profileId, ct)`, `TestConnectionAsync(profileId, ct)`, `SendAsync(request, ct)`, `StreamAsync(request, ct)`.
- `IAiProvider`: stable `ProviderKind` plus the same provider operations with a resolved `AiProfile`.
- `AiProfile`: ID, display name, provider kind, base URL, model ID, auth mode, secret reference, timeout, enabled state, and explicit local-endpoint permission.
- `AiRequest`: profile ID, optional model override, ordered messages, and requested text/JSON format. No arbitrary HTTP payload or domain-specific fields.
- `IAiProfileStore` and `IAiSecretResolver` are host-supplied contracts.
- `AddOmegaAiGateway(...)` registers the Gateway and an explicit provider list; no reflection discovery.

- [ ] **Step 1: Write contract-shape tests** asserting provider-neutral DTOs have no OmegaIllustrator/SVG references, messages preserve order/roles, and request model override is optional.
- [ ] **Step 2: Run the focused test** with `dotnet test tests/Omega.AI.Gateway.Tests --filter FullyQualifiedName~ContractShapeTests`; verify it fails because the project/contracts do not yet exist.
- [ ] **Step 3: Create the solution, projects, baseline configuration, contracts, and DI builder** using only dependencies required for DI and tests; set package metadata so `dotnet pack` is supported.
- [ ] **Step 4: Run focused tests and `dotnet build Omega.AI.Gateway.sln`**; verify the solution builds and contract tests pass.

### Task 2: Implement Gateway routing, validation, and normalized errors

**Files:**
- Create: `src/Omega.AI.Gateway/Gateway/AiGateway.cs`
- Create: `src/Omega.AI.Gateway/Errors/AiGatewayException.cs`
- Create: `src/Omega.AI.Gateway/Errors/AiErrorCode.cs`
- Create: `src/Omega.AI.Gateway/Providers/AiProviderRegistry.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Gateway/AiGatewayTests.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Fakes/FakeAiProvider.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Fakes/FakeAiProfileStore.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Fakes/FakeSecretResolver.cs`

**Interfaces:**
- `AiGateway` resolves enabled profiles, finds exactly one provider by `ProviderKind`, applies the profile timeout, validates profile/model consistency, and forwards cancellation.
- Normalized error codes distinguish configuration, unsupported capability, authentication/authorization, rate limit, network/provider failure, timeout, and malformed response.

- [ ] **Step 1: Write tests** for profile resolution, disabled/unknown profiles, missing/duplicate providers, model override, timeout, cancellation, and redaction of a sentinel secret from normalized errors.
- [ ] **Step 2: Run `dotnet test ... --filter FullyQualifiedName~AiGatewayTests`** and verify the behavior tests fail before implementation.
- [ ] **Step 3: Implement Gateway routing, provider registry validation, timeout linking, and safe error translation**; preserve `OperationCanceledException` semantics.
- [ ] **Step 4: Run focused tests and full solution build**; verify each routing/error case passes without contacting a network.

### Task 3: Add the OpenAI-compatible Chat Completions adapter

**Files:**
- Create: `src/Omega.AI.Gateway/Providers/OpenAiCompatible/OpenAiCompatibleProvider.cs`
- Create: `src/Omega.AI.Gateway/Providers/OpenAiCompatible/OpenAiCompatibleOptions.cs`
- Create: `src/Omega.AI.Gateway/Providers/OpenAiCompatible/OpenAiCompatibleProtocol.cs`
- Create: `src/Omega.AI.Gateway/Providers/OpenAiCompatible/SseReader.cs`
- Create: `src/Omega.AI.Gateway/Providers/OpenAiCompatible/OpenAiEndpointPolicy.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Providers/OpenAiCompatibleProviderTests.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Providers/SseReaderTests.cs`

**Interfaces:**
- Provider kind is `openai-compatible`; base URL is configuration, API key is resolved from `SecretReference` immediately before each request.
- Supports Chat Completions `POST /chat/completions`, optional `GET /models`, connection checks, text/JSON response format, and streaming deltas.
- `HttpClient` is injected; all network operations accept and honor cancellation.

- [ ] **Step 1: Write fake-handler tests** for request JSON/auth header, completion/usage parsing, model discovery, missing model-list fallback, HTTP errors, SSE deltas/terminal events, and malformed streams.
- [ ] **Step 2: Run the focused provider tests** and verify they fail before adapter implementation.
- [ ] **Step 3: Implement request mapping, response mapping, SSE parsing, bounded response diagnostics, and endpoint policy**; allow HTTPS by default and only explicit loopback HTTP, with redirects disabled for credential-bearing requests.
- [ ] **Step 4: Run focused tests and solution build**; verify requests never contain a secret in URLs or diagnostics and no external network is used.

### Task 4: Implement official ChatGPT OAuth authorization and credential lifecycle

**Files:**
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/ChatGptOAuthOptions.cs`
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/ChatGptOAuthCredential.cs`
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/IChatGptOAuthCredentialStore.cs`
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/ChatGptOAuthAuthorizationService.cs`
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/LoopbackOAuthCallbackListener.cs`
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/IAuthorizationBrowser.cs`
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/SystemAuthorizationBrowser.cs`
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/ChatGptOAuthTokenClient.cs`
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/ChatGptIdTokenValidator.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Providers/ChatGptOAuthAuthorizationTests.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Providers/ChatGptOAuthTokenTests.cs`

**Interfaces:**
- `IChatGptOAuthCredentialStore` provides `GetAsync(profileId)`, `SaveAsync(profileId, credential)`, and `DeleteAsync(profileId)`; production implementations belong to the host and must protect token material.
- `BeginSignInAsync(profileId, hostId, ct)` creates a short-lived loopback callback listener, builds the official authorization URI, and opens the system browser through an injectable `IAuthorizationBrowser`; a headless host may select a browser adapter that exposes the URI instead.
- `WaitForSignInAsync(profileId, ct)` observes callback completion; the listener accepts only `127.0.0.1` and the fixed `/auth/callback` path.
- Callback processing validates state/client ID, exchanges authorization code with PKCE, verifies ID-token signature/issuer/audience/expiry/nonce and required granted scope, then stores the credential.
- OAuth attempt state, verifier, and nonce are one-time and short-lived. The default adapter launches the system browser and accepts only the fixed loopback callback; headless hosts can inject a browser adapter.

- [ ] **Step 1: Write deterministic tests** for PKCE challenge generation, browser launch contract, exact `127.0.0.1` callback URI handling, state/nonce binding, dynamic versus issued client ID, `access_denied`, required scopes, token refresh rotation, and ID-token validation failures.
- [ ] **Step 2: Run focused OAuth tests** and verify they fail before implementation.
- [ ] **Step 3: Implement authorization/session state and token exchange** against the official OpenAI endpoints; use a maintained OIDC/JWT library for signature validation against published JWKS rather than custom cryptography.
- [ ] **Step 4: Implement serialized per-profile refresh** and replace access/refresh/ID tokens plus expiry/scopes together only after a successful refresh.
- [ ] **Step 5: Run focused OAuth tests and solution build**; use generated test signing keys and fake `HttpMessageHandler`, never a real account or token.

### Task 5: Add ChatGPT OAuth model discovery and Responses streaming adapter

**Files:**
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/ChatGptOAuthProvider.cs`
- Create: `src/Omega.AI.Gateway/Providers/ChatGptOAuth/ResponsesProtocol.cs`
- Create: `tests/Omega.AI.Gateway.Tests/Providers/ChatGptOAuthProviderTests.cs`

**Interfaces:**
- Provider kind is `chatgpt-oauth`; `GetModelsAsync` reads the account-specific public `/v1/models` catalog and returns only displayable (`visibility=list`) entries.
- `TestConnectionAsync` uses authorized model discovery without consuming an inference request.
- `SendAsync` collects the text stream and completes only on `response.completed`; `StreamAsync` forwards normalized text deltas and terminal/failure events.

- [ ] **Step 1: Write tests** for model filtering, bearer auth, `/v1/responses` payload (`store=false`, `stream=true`), message/instructions mapping, omission of unsupported preview fields, terminal success, failure after deltas, usage-limit errors, refresh-on-expiry, and no silent alternate-provider retry.
- [ ] **Step 2: Run focused provider tests** and verify failures before implementation.
- [ ] **Step 3: Implement public Responses API mapping and SSE event translation**; reject unsupported request capabilities and preserve safe provider error code/status/request ID.
- [ ] **Step 4: Run focused tests and solution build**; verify success is impossible without `response.completed` and all requests use only official URLs.

### Task 6: Document consumers, OAuth eligibility, and package verification

**Files:**
- Create: `README.md`
- Create: `PROJECT.md`
- Create: `AGENTS.md`
- Create: `docs/ARCHITECTURE.md`
- Create: `docs/SECURITY.md`
- Create: `docs/CHATGPT_OAUTH_TESTING.md`
- Modify: `Directory.Build.props` and package metadata if pack checks reveal gaps
- Test: full `tests/Omega.AI.Gateway.Tests`

- [ ] **Step 1: Write documentation checks or reviewable examples** for dependency injection, implementing/registering a custom provider, supplying profile/secret stores, and starting the OAuth sign-in flow.
- [ ] **Step 2: Verify docs state the OAuth boundary**: no API key is created or required for ChatGPT plan OAuth; eligible open-source/local test use only; remote/self-hosted VM must authorize locally and transfer protected credentials securely with its own stable host ID; paid or remotely hosted offerings require OpenAI review; no cookies/private endpoints/limit evasion.
- [ ] **Step 3: Document OAuth setup using the official loopback callback and actual preview constraints**, including token protection, account/usage limits, unsupported request fields, `response.completed`, and recovery for permission/rate-limit failures.
- [ ] **Step 4: Run `dotnet test Omega.AI.Gateway.sln` and `dotnet pack src/Omega.AI.Gateway/Omega.AI.Gateway.csproj -c Release -o artifacts/packages`**; verify both succeed without secrets or external requests.
- [ ] **Step 5: Inspect the final diff and Git status**, check package contents do not include test secrets/runtime state, and report tested behavior plus OAuth eligibility and live-validation limitations.

## Official OAuth implementation references

- https://developers.openai.com/siwc/token-sharing-open-source
- https://developers.openai.com/siwc/token-sharing-open-source/sign-in
- https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
- https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms
- https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations
- https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery
