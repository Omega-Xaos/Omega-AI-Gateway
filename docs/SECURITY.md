# Security

## Credentials

- Profiles store secret references, never secret values.
- `IAiSecretResolver` and `IChatGptOAuthCredentialStore` are host-provided; the host must use an OS credential store or an equivalent protected server secret store and make credential saves atomic.
- Register the OAuth credential store as a singleton or otherwise guarantee that its refresh lock and atomic save coordinate all application instances sharing a credential.
- Avoid logging request headers, token responses, raw provider error bodies or OAuth callback query strings.
- Gateway-normalized errors do not retain the provider exception as an inner exception.

## Network endpoints

- The OpenAI-compatible adapter requires HTTPS, except HTTP to an explicitly enabled loopback endpoint for a local model service.
- Anthropic and Gemini adapters validate the configured base URL before resolving a key and send credentials only in their documented API-key headers.
- Built-in DI registration disables redirects before credentials can be forwarded. Keep this registration or configure an equivalent handler if constructing providers manually.
- ChatGPT OAuth endpoints are fixed to the official OpenAI hosts and validated when options are used.
- A public multi-tenant host must enforce a provider endpoint allow-list and egress policy to prevent SSRF.

## ChatGPT OAuth

- Authorization uses system-browser consent, authorization code + PKCE, random per-attempt state and nonce, and a short-lived loopback callback bound to `127.0.0.1` at `/auth/callback`.
- ID tokens are signature-validated against OpenAI's published JWKS, with issuer, client audience, expiry and nonce checks.
- Access, refresh and ID tokens are stored only by the host's protected credential store. Rotating tokens are replaced together after successful refresh.
- `AcquireRefreshLockAsync` must serialize refresh across every process that can use a credential store. After acquiring the lock, the library rereads the latest credential before rotating it.
- Keep one OAuth authorization service process available across `BeginSignInAsync` and `WaitForSignInAsync`; the temporary callback attempt is in-memory and expires automatically.
- The direct-use scope is required before model discovery or inference.
- Never use cookies, automate the ChatGPT website, access private `backend-api` endpoints, bypass usage limits or silently change billing/authentication paths.
