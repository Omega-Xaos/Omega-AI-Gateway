# Architecture

`IAiGateway` is the consumer-facing contract. It resolves an enabled `AiProfile`, chooses exactly one `IAiProvider` by `ProviderKind`, applies the profile timeout and forwards cancellation. Provider adapters map neutral DTOs to the transport protocol and translate responses back to neutral DTOs.

```text
Desktop app / ASP.NET host
  ├─ IAiProfileStore (host-owned)
  ├─ IAiSecretResolver (host-owned)
  ├─ IChatGptOAuthCredentialStore (host-owned, protected)
  └─ IAiGateway
       ├─ OpenAiCompatibleProvider → Chat Completions protocol
       ├─ ChatGptOAuthProvider → public Responses API
       └─ custom IAiProvider implementations
```

Provider registrations are explicit. `AddOpenAiCompatible` and `AddChatGptOAuth` configure HTTP clients with automatic redirects disabled. A custom provider is registered with `AddProvider<TProvider>()` and is responsible for safe endpoint and credential handling.

Profiles contain configuration only, including a reference to a host-managed secret when a provider requires one. The OAuth credential record is kept behind `IChatGptOAuthCredentialStore`; the library intentionally does not prescribe a storage backend. The host store provides atomic writes and a lock that coordinates token refresh and reauthorization across all instances sharing a credential. A pending replacement ID token is persisted alongside rotated credentials before validation so a temporary JWKS outage does not discard a newly rotated refresh token; the adapter will not infer until that identity token is validated.

The singleton OAuth authorization service holds short-lived callback state in process memory. A begin and wait operation must reach the same live instance. A horizontally scaled host must provide session affinity or place the authorization flow on one designated instance; distributed authorization-attempt state is outside this library release.

The OpenAI-compatible adapter implements Chat Completions, which is a protocol supported by many endpoints, not a guarantee that every provider implements the same semantics. The ChatGPT plan adapter is separate because OAuth, Responses streaming and preview restrictions differ from API-key-backed Chat Completions.
