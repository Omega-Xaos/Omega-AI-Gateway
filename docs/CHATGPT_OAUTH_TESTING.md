# ChatGPT OAuth local testing

## Eligibility and user consent

The integration is an optional test/development route for apps that are eligible under the current OpenAI Sign in with ChatGPT preview terms. It uses no separate OpenAI API key. A ChatGPT Plus plan and the app-limit screen do not by themselves prove eligibility or guarantee a particular model, limit or availability. Check the official [Sign in with ChatGPT overview](https://developers.openai.com/siwc/token-sharing-open-source) and [registration and sign-in guide](https://developers.openai.com/siwc/token-sharing-open-source/sign-in) before enabling the path in a consumer.

The app creates a fresh PKCE verifier, state and nonce, starts an HTTP listener bound to loopback, then opens the system browser. The user signs in and approves requested permissions on OpenAI's page. The callback is exactly `/auth/callback` on `127.0.0.1`; it is one-time, checked against state and then exchanged using the same exact callback URI. The validated issued client ID and stable host ID are retained by the host's credential store.

The sign-in service keeps a short-lived attempt in process memory between `BeginSignInAsync` and `WaitForSignInAsync`, so both calls must reach the same running instance. Horizontally scaled hosts need session affinity or a designated OAuth instance. The service itself is registered as a singleton, and its credential store must implement atomic persistence plus a refresh lock shared by all processes using those credentials.

The granted token must include `chatgpt.tokens.use.direct`. The library validates ID-token signature, issuer, audience, expiry and nonce before saving credentials. Store tokens using protected OS storage on a desktop. Do not copy callback URLs, authorization codes or tokens into logs, issue trackers or source control.

## Desktop and server usage

For local desktop tests, use the default system browser adapter and a protected local credential store. Tests never launch the real browser or use a real account.

For a self-hosted VM without a desktop browser, complete OAuth locally and transfer the protected credential through a secure channel. Keep the VM's own stable `ext_agent_host_id` with its runtime and allow that VM to perform refresh. Do not point a remote host at the desktop's loopback listener.

Paid or remotely hosted consumer offerings require the applicable OpenAI review. Do not ship this preview path as an implicit billing substitute for an API integration.

## Inference behavior and limits

The adapter reads `/v1/models` for the selected account and exposes only entries whose `visibility` is `list`. It uses only the public `/v1/responses` endpoint, with `store=false` and `stream=true`, and waits for `response.completed` before returning success. Unsupported preview request options are rejected. Permission, usage-limit, incomplete and interrupted responses remain errors; the adapter never retries through another provider or billing identity. Per-app limits in ChatGPT settings are user controls; they do not create an extra usage allowance, and Plus usage remains shared across participating apps under current plan rules.

See the official [models and inference guide](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), [self-hosted VM guidance](https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms), and [preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations) for current behavior.
