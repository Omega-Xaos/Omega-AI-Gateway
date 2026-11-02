# Omega AI Gateway

Provider-neutral .NET 10 library for connecting desktop and server applications to explicitly registered AI providers.

The library currently contains:

- neutral profiles, messages, requests, responses, model metadata and streaming events;
- gateway routing, cancellation, timeouts and normalized errors;
- an OpenAI-compatible Chat Completions adapter for services that expose that protocol;
- an optional official Sign in with ChatGPT adapter for eligible local development and testing.

The gateway does not contain SVG, illustration or other consumer-domain behavior. A consuming application validates model output for its own domain.

## Add to an application

Reference the project or the packed NuGet package. The host implements profile and protected credential storage:

```csharp
services.AddSingleton<IAiProfileStore, ApplicationAiProfileStore>();
services.AddSingleton<IAiSecretResolver, ApplicationSecretResolver>();
services.AddOmegaAiGateway(builder => builder.AddOpenAiCompatible());
```

For ChatGPT OAuth in an eligible local test application, register the host's protected OAuth credential store and add the OAuth adapter:

```csharp
services.AddSingleton<IChatGptOAuthCredentialStore, ProtectedChatGptCredentialStore>();
services.AddOmegaAiGateway(builder => builder.AddChatGptOAuth(new ChatGptOAuthOptions()));
```

The authorization service is a singleton because an OAuth attempt spans the browser round trip. `BeginSignInAsync` and `WaitForSignInAsync` must reach the same running process. The service opens the system browser. The user signs in and approves the app. The library receives the browser's one-time redirect through an HTTP loopback listener on `127.0.0.1`; applications inject another `IAuthorizationBrowser` for headless or controlled environments. Store OAuth credentials using OS-protected storage on desktop or the host's secret store on a server. The store must provide atomic writes and a refresh lock shared by all processes that can use the same credentials. Do not serialize tokens into `AiProfile` or logs.

```csharp
var signIn = await authorization.BeginSignInAsync(profileId, stableHostId, cancellationToken);
var credential = await authorization.WaitForSignInAsync(profileId, cancellationToken);
var response = await gateway.SendAsync(new AiRequest(
    profileId,
    [new AiMessage(AiMessageRole.User, "Describe the task")]), cancellationToken);
```

An application can add a provider without changing gateway contracts:

```csharp
services.AddOmegaAiGateway(builder => builder.AddProvider<ContosoNativeProvider>());
```

`IAiProvider` is the transport boundary. Vendor APIs with different request, auth or streaming semantics need their own adapter; they are not treated as automatically compatible.

## ChatGPT plan OAuth boundary

This optional integration does not create or require an OpenAI API key. It uses an explicit system-browser sign-in, official OAuth endpoints, PKCE, a one-time loopback callback and the public Responses API. It rejects accounts without the granted `chatgpt.tokens.use.direct` scope. Inference is sent with `store=false` and `stream=true`, and succeeds only after `response.completed`.

The integration is for eligible open-source/local testing workflows under OpenAI's current rules. A ChatGPT Plus subscription or the per-app limits visible in the account UI do not by themselves guarantee OAuth eligibility, model access or unlimited use. Paid or remotely hosted consumer products must obtain the required OpenAI review. The adapter does not automate ChatGPT's website, extract cookies, use private endpoints, evade limits or silently retry under another billing identity.

For a self-hosted VM, complete OAuth locally, transfer credentials through a protected channel, and let the VM refresh them using its own stable host ID. See [OAuth testing and deployment](docs/CHATGPT_OAUTH_TESTING.md).

## Build and verify

```powershell
dotnet test Omega.AI.Gateway.sln
dotnet pack src/Omega.AI.Gateway/Omega.AI.Gateway.csproj -c Release -o artifacts/packages
```

All automated tests use in-process HTTP handlers and generated test keys. No provider account, API key, or external inference request is required.
