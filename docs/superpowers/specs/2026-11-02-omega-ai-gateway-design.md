# Omega AI Gateway Library Design

## Intent

Create a standalone, reusable .NET library at `D:\10_WORK\00_PROJECTS\00_ACTIVE\DLL_OMEGA_LAB_CONNECT_AI` that gives Omega Lab applications one stable way to call text-based AI providers. The first consumer is expected to be the OmegaIllustrator SVG service, but the library must not depend on SVG, illustrations, scenes, assets, or any other consumer domain.

The employer's eventual deliverable is a working local SVG composition service: it selects only approved existing vector assets by named objects or by meaning, lets the user edit a composition, and exports editable vector SVG. The AI Gateway is one reusable integration component of that larger service. It chooses or calls models; consumer-specific code validates domain results and performs domain actions.

## Goals

- Define provider-neutral contracts for profiles, models, messages, requests, responses, usage, connection checks, and streaming events.
- Let a consumer select a profile and model without depending on a provider SDK or HTTP API shape.
- Let a new provider be added as an `IAiProvider` adapter and registered with dependency injection, without changing consumer code or Gateway contracts.
- Deliver a first useful adapter for OpenAI-compatible Chat Completions endpoints, including model discovery when supported, connection checks, non-streaming completion, and streaming.
- Provide an optional official Sign in with ChatGPT adapter for permitted local development and testing, using the public Responses API and its current constraints.
- Support user-authorized ChatGPT plan usage through the same system-browser consent flow used by the user's existing local applications; no OpenAI API key is required for this path.
- Keep credentials outside profile JSON, source, browser code, and logs by resolving a `SecretReference` through a host-provided secret resolver.
- Keep failures, cancellation, timeouts, and provider diagnostics explicit.
- Make the library usable from other .NET 10 applications and packable as a NuGet package when a consuming product chooses to publish it.

## Non-goals for this first library release

- Building the OmegaIllustrator web service, SVG editor, asset importer, or scene-selection logic.
- Claiming that every AI API is interchangeable or implementing every vendor adapter up front. Incompatible native APIs need their own adapters.
- Implementing Anthropic, Gemini, image/audio/multimodal APIs, or autonomous agent/tool execution in the first release.
- Treating ChatGPT OAuth as a replacement for an OpenAI API key, a way to obtain unlimited use, a way around plan/API limits, or a default for a public/paid/remote-hosted product.
- Persisting profiles or secrets in a specific machine store. The host owns profile persistence and supplies secret resolution; Windows DPAPI/Credential Manager and server secret stores can be added by host integrations.
- Loading arbitrary assemblies dynamically as plugins. Extensibility is explicit through provider implementations and DI registration.
- Adding databases, a web UI, user accounts, or a standalone gateway server.

## Constraints

- C# and .NET 10 (`net10.0`) follow the supplied OmegaIllustrator specification and installed SDK.
- The library must remain independent from OmegaIllustrator and its domain model.
- API keys and OAuth tokens must never be stored in `AiProfile` or emitted in diagnostics.
- HTTPS is the default for remote endpoints. Plain HTTP is allowed only for explicitly configured local model endpoints. Endpoint handling must not silently forward credentials across redirects or to a different origin.
- Provider-specific capabilities and unsupported options must be represented or rejected clearly, not silently treated as universally supported.
- No live provider call is required for a deterministic build and automated test run.
- The user explicitly declined creating an OpenAI API key for this project. Implementation and automated tests must not create, inspect, or use one; live testing uses ChatGPT OAuth only after the user completes system-browser consent.
- ChatGPT plan usage through Sign in with ChatGPT is preview functionality whose current eligibility and deployment restrictions must be checked against official OpenAI documentation before use. The intended use here is local development/testing; normal application usage should prefer official provider APIs and their supported authentication/billing path.

## Approaches considered

1. **One standalone library with a stable contract and separately registered adapters — selected.** Put the reusable Gateway contracts, orchestration, OpenAI-compatible adapter, and DI registration in the requested project. Consumers reference the project or its NuGet package; native provider adapters can live in separate assemblies. This keeps initial adoption simple while preserving a clean extension boundary.
2. **A single universal HTTP implementation for all AI services.** Rejected because vendor APIs differ in authentication, model discovery, streaming, request options, and response formats. Treating them as one wire protocol would misrepresent compatibility and make errors hard to diagnose.
3. **A multi-project framework with one assembly/project per provider from day one.** Deferred because there is currently only one specified wire-compatible adapter. Splitting immediately adds packaging and version coordination without helping the first consumer; the `IAiProvider` registration boundary supports a later split.

## Chosen design

### Workspace and public boundary

Create a conventional standalone .NET library repository at the requested path, initially containing one production project and one test project. The production project is packable and targets `net10.0`. Public names use the `Omega.AI.Gateway` namespace.

The public Gateway surface includes `IAiGateway`, `IAiProvider`, `AiProfile`, `AiModelInfo`, `AiMessage`, `AiRequest`, `AiResponse`, `AiUsage`, `AiStreamEvent`, and `AiConnectionTest`. `IAiGateway` provides profile-based model listing, connection testing, completion, and streaming. `IAiProvider` owns provider-specific transport behavior. A profile is configuration metadata and contains a secret reference, never the secret itself.

The host supplies an `IAiProfileStore` for profile lookup and `IAiSecretResolver` for resolving a secret reference at call time. The library provides Gateway orchestration and DI registration, but does not decide how profile data or secret material is persisted. Multiple profiles may use the same provider kind. A request may override the profile's configured model with a validated model ID.

The Gateway selects a provider by the profile's `ProviderKind`, validates request/profile consistency, applies a configured timeout, forwards cancellation, and returns normalized provider results. Streaming uses `IAsyncEnumerable<AiStreamEvent>` and propagates cancellation. Errors distinguish configuration, unsupported capability, authentication/authorization, rate limit, provider/network failure, timeout, malformed response, and cancellation, while avoiding secret values.

### First provider adapters

`OpenAiCompatibleProvider` initially targets the Chat Completions API. It accepts a configured base URL and model ID; it can query `/models` when available, but manual model IDs remain valid. It maps the provider-neutral message and response-format contract to the wire format and translates responses, usage, finish reasons, and SSE deltas to Gateway DTOs.

Compatibility is an adapter capability, not an assumption about a vendor name. A successful real connection test is the evidence that a particular endpoint/model works. The adapter does not claim support for native APIs with incompatible protocols.

`ChatGptOAuthProvider` is a separate optional adapter for eligible local development/testing. It uses the official Sign in with ChatGPT authorization-code + PKCE flow, validates state/nonce/signature/audience/scopes, refreshes rotating tokens through a host-provided protected credential store, lists account-visible models, and calls only the public Responses API. For each inference request it sets `store=false` and `stream=true`, omits currently unsupported fields, and reports success only after `response.completed`. It surfaces permission, policy, admission, and usage-limit failures without retrying through another billing identity.

### Extending to other providers

A consumer or provider package implements `IAiProvider`, declares a stable `ProviderKind`, and registers it with the Gateway. The consuming application does not acquire SVG-specific or vendor-specific branches. The ChatGPT OAuth service follows the user's existing local-app flow: it creates a short-lived `127.0.0.1` callback listener, builds the authorization URI, and opens it through an injectable system-browser abstraction. Headless or web hosts can instead receive the URI and manage browser launch through their own adapter. For a self-hosted remote VM, OAuth is completed locally and protected credentials are transferred securely; the VM uses its own stable host ID and owns subsequent refresh. Public/paid/remote-hosted consumer products must not enable this adapter unless OpenAI has approved that deployment path. No browser automation, cookie extraction, ChatGPT private endpoints, hidden billing-path fallback, or OAuth credential logging is allowed.

The current official guidance describes this plan-usage OAuth path for open-source and locally hosted apps; paid or remotely hosted offerings require OpenAI review. The target library is not itself permission to use the flow in every consumer. The consumer must independently confirm eligibility. This project will not create or configure an API key; adapters for other providers remain available through the provider interface.

### Security and network behavior

Secrets are resolved just before use and attached only to the provider request. They are never included in exception messages, logs, model output, or response diagnostics. HTTP is rejected by default; explicitly local profiles may enable loopback HTTP for local model servers. Redirects that could move a credential-bearing request to another origin are not followed. Host applications deploying this library for untrusted users must enforce endpoint allow-lists or an endpoint policy to prevent SSRF; the local OmegaIllustrator use case is not a public multi-tenant gateway.

### Tests and validation

The test project uses a fake provider and a local in-process HTTP handler/server to verify profile/provider selection, model override, normalized completion, streaming, cancellation, timeout/error mapping, secret redaction, endpoint rules, and request/response serialization. Tests must not require API credentials or external network access. The first release is accepted when the library and tests build, the automated tests pass, and a short consumer example shows how to register a provider and call the generic contract.

## Builder decisions

- Use .NET 10 only in the initial package, matching the supplied service target. Add other target frameworks only when an actual Omega Lab consumer requires them.
- Keep provider adapters explicitly registered; do not use reflection-based plugin discovery.
- Keep persistence and secret storage interfaces in the Gateway boundary and implementations in the consuming host or separate integration package.
- Implement the approved library stage: generic contracts, an OpenAI-compatible Chat Completions adapter, and an optional official ChatGPT OAuth adapter for eligible local testing. Native adapters remain future provider packages.
- Do not connect OmegaIllustrator or add its source tree in this phase.

## Expected final outcomes

This phase delivers a buildable, tested standalone AI Gateway library, an OpenAI-compatible adapter, an optional official ChatGPT OAuth test adapter, and consumer-facing documentation. The documentation recommends supported provider APIs for ordinary application use and explains that ChatGPT plan OAuth is a constrained local development/testing option subject to official eligibility, shared usage limits, and preview behavior. The larger employer assignment remains a separate product milestone: a local Blazor service, verified real SVG assets exported from the Figma source, editing/layout/palette behavior, semantic and explicit-object generation, editable SVG export, and a recorded or hosted demonstration. Completion of this library alone does not satisfy the employer's full assignment.

## Official OpenAI references used for the OAuth adapter

- [Sign in with ChatGPT overview](https://developers.openai.com/siwc/token-sharing-open-source)
- [Registration and sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [Accounts and sessions](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions)
- [Models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- [Self-hosted VMs](https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms)
- [Preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations)
- [Errors and recovery](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery)
